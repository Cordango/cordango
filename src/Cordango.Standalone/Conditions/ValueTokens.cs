// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Globalization;
using System.Text.RegularExpressions;

namespace Cordango.Standalone.Conditions;

/// <summary>
/// The placeholders a definition may write where a value goes: who is asking, and when.
///
/// <para><c>{{actor.id}}</c>, <c>{{today}}</c>, <c>{{now}}</c>, and the offsets <c>{{today+7}}</c>,
/// <c>{{today-30d}}</c>, <c>{{today+2w}}</c>, <c>{{now-4h}}</c>. They appear in a command's
/// <c>set</c>, in a workflow's effects, in a condition's <c>value</c> and in a notification's text,
/// which is why they resolve in one place rather than four.</para>
///
/// <para><b>This grammar has to match the gate's, and for a long time it did not.</b> The compiler's
/// <c>ExprTokens</c> accepts a unit suffix — <c>d</c>, <c>w</c>, <c>h</c> — and this class matched
/// only bare digits. So <c>{{today+1w}}</c> passed <c>cordango check</c>, passed
/// <c>cordango build</c>, and then resolved to nothing: the literal text <c>{{today+1w}}</c> was
/// written into the field. Every failure it caused was silent. In a condition the literal never
/// equals a date, so the automation simply never fired. In an effect the write threw, and
/// <c>WorkflowRunner</c> catches effect failures and logs them — the record saved, the next record
/// was never created, and the screen said nothing at all.</para>
///
/// <para><b>Why the two are not one class.</b> <c>ExprTokens</c> lives in <c>Cordango.Compiler</c>,
/// and a generated application must not carry the compiler — the schemas, the gate and the whole
/// Cord parser — to resolve a date. So there are deliberately two implementations of one grammar,
/// and <c>TokenGrammarTests</c> is what holds them together: it generates every shape the gate could
/// accept, asks <c>ExprTokens</c> which ones it allows, and fails if this class does not resolve
/// each of them to the same value. Add a unit there and that test fails until it is added here.</para>
///
/// <para><b>Everything comes from the clock that was passed in.</b> Nothing here reads
/// <c>DateTime.Now</c>, so a test can ask what a workflow would have done last March, and two
/// applications processing the same record at the same instant agree.</para>
/// </summary>
public static class ValueTokens
{
    /// <summary>A whole value that is nothing but one token: <c>"{{today}}"</c>. Returns the input
    /// unchanged when it is not.</summary>
    public static string? Resolve(string? value, string? actorId, string? userId, DateTimeOffset now)
    {
        if (value is null || value.Length < 5) return value;
        if (!value.StartsWith("{{", StringComparison.Ordinal) || !value.EndsWith("}}", StringComparison.Ordinal))
            return value;

        return Token(value[2..^2].Trim(), actorId, userId, now) ?? value;
    }

    /// <summary>Every token inside a longer string: <c>"Due {{today+7}}, raised by {{actor.id}}"</c>.
    /// A token nothing answers is left as written rather than blanked, so a mistyped one is visible
    /// in the output instead of turning into a silent gap.</summary>
    /// <param name="record">Reads a field of the record a rule is about, for <c>{{record.x}}</c>.</param>
    /// <param name="source">Reads a field of the row being iterated, for <c>{{source.x}}</c> — the
    /// month of a plan, the lifecycle step of a grid. Absent outside a <c>createForEach</c>.</param>
    /// <param name="created">Reads a field of the record an EARLIER effect in the same list just
    /// inserted, for <c>{{created.id}}</c>. Absent until something has been created, which is what
    /// makes a rule that creates a parent and then points its children at it expressible at all: the
    /// id does not exist when the definition is written and there is no other way to name it.</param>
    public static string? Fill(
        string? template,
        string? actorId,
        string? userId,
        DateTimeOffset now,
        Func<string, string?>? record = null,
        Func<string, string?>? source = null,
        Func<string, string?>? created = null)
    {
        if (template is null) return null;

        return Placeholder.Replace(template, match =>
        {
            var token = match.Groups[1].Value.Trim();

            if (record is not null && token.StartsWith("record.", StringComparison.Ordinal))
                return record(token["record.".Length..]) ?? "";

            if (source is not null && token.StartsWith("source.", StringComparison.Ordinal))
                return source(token["source.".Length..]) ?? "";

            if (created is not null && token.StartsWith("created.", StringComparison.Ordinal))
                return created(token["created.".Length..]) ?? "";

            return Token(token, actorId, userId, now) ?? match.Value;
        });
    }

    private static string? Token(string token, string? actorId, string? userId, DateTimeOffset now)
    {
        // Both spellings, because both are in the corpus and neither is wrong. The platform resolves
        // them identically.
        if (token is "actor.id" or "currentUser.id") return actorId ?? "";
        if (token is "actor.userId" or "currentUser.userId") return userId ?? "";

        if (Relative.Match(token) is not { Success: true } match) return null;

        var anchor = match.Groups[1].Value;
        var unit = match.Groups[4].Value;

        // An hour offset on a date anchor would resolve to the day it started on: the author meant
        // {{now-4h}}. The gate refuses that pairing, and leaving it unresolved here rather than
        // quietly agreeing is what keeps the two answers the same.
        if (anchor == "today" && unit == "h") return null;

        // A date anchor is a DATE — midnight of the day the clock is on, in UTC. Taking the offset's
        // own wall-clock date instead would make the same instant resolve to two different days for
        // two callers, which is the kind of thing that shows up as one row missing from a report.
        var at = anchor == "today" ? Today(now).ToDateTime(TimeOnly.MinValue) : now.UtcDateTime;

        if (match.Groups[2].Success)
        {
            var amount = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
            if (match.Groups[2].Value == "-") amount = -amount;

            at = unit switch
            {
                // No month or year, deliberately, and this is the one place it would be easy to add.
                // JavaScript's setMonth overflows (Jan 31 + 1m = Mar 3) and .NET's AddMonths clamps
                // (Feb 28), so a {{today+1m}} filter would select different rows in the browser than
                // on the server. Weeks are exactly seven days on both sides and carry no such risk.
                "w" => at.AddDays(amount * 7),
                "h" => at.AddHours(amount),
                _ => at.AddDays(amount),
            };
        }

        return anchor == "today"
            ? at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : new DateTimeOffset(at, TimeSpan.Zero).ToString("o", CultureInfo.InvariantCulture);
    }

    private static DateOnly Today(DateTimeOffset now) => DateOnly.FromDateTime(now.UtcDateTime);

    private static readonly Regex Placeholder = new(@"\{\{([^}]+)\}\}", RegexOptions.Compiled);

    /// <summary>The same shape <c>ExprTokens.Relative</c> matches. Kept spelled out rather than
    /// shared, because sharing it would mean shipping the compiler inside every generated
    /// application; <c>TokenGrammarTests</c> is what stops the two drifting apart instead.</summary>
    private static readonly Regex Relative = new(
        @"^(today|now)(?:\s*([+-])\s*(\d{1,5})\s*([dwh])?)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
}

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
/// <para><c>{{actor.id}}</c>, <c>{{today}}</c>, <c>{{now}}</c>, and the date offsets
/// <c>{{today+7}}</c> / <c>{{today-30}}</c>. They appear in a command's <c>set</c>, in a workflow's
/// effects, in a condition's <c>value</c> and in a notification's text, which is why they resolve in
/// one place rather than three.</para>
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
    public static string? Fill(string? template, string? actorId, string? userId, DateTimeOffset now, Func<string, string?>? record = null)
    {
        if (template is null) return null;

        return Placeholder.Replace(template, match =>
        {
            var token = match.Groups[1].Value.Trim();

            if (record is not null && token.StartsWith("record.", StringComparison.Ordinal))
                return record(token["record.".Length..]) ?? "";

            return Token(token, actorId, userId, now) ?? match.Value;
        });
    }

    private static string? Token(string token, string? actorId, string? userId, DateTimeOffset now)
    {
        // Both spellings, because both are in the corpus and neither is wrong. The platform resolves
        // them identically.
        if (token is "actor.id" or "currentUser.id") return actorId ?? "";
        if (token is "actor.userId" or "currentUser.userId") return userId ?? "";
        if (token == "now") return now.ToString("O", CultureInfo.InvariantCulture);
        if (token == "today") return Today(now).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        // "today+7", "today-30". Days, because that is the unit every use of it in the corpus means
        // and a unit-less number that silently meant hours would be worse than no offset at all.
        if (Offset.Match(token) is { Success: true } offset)
        {
            var days = int.Parse(offset.Groups[2].Value, CultureInfo.InvariantCulture);
            if (offset.Groups[1].Value == "-") days = -days;
            return Today(now).AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        return null;
    }

    private static DateOnly Today(DateTimeOffset now) => DateOnly.FromDateTime(now.UtcDateTime);

    private static readonly Regex Placeholder = new(@"\{\{([^}]+)\}\}", RegexOptions.Compiled);

    private static readonly Regex Offset = new(@"^today\s*([+-])\s*(\d{1,5})$", RegexOptions.Compiled);
}

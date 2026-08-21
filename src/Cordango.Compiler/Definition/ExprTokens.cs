// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Globalization;
using System.Text.RegularExpressions;

namespace Cordango.Definition;

/// <summary>
/// The value tokens an authored condition or filter may carry — the ONE definition of that grammar,
/// shared by the gate (author time) and <c>ConditionEvaluator</c> (run time) so a token can never
/// validate and then fail to resolve. The renderer implements the same grammar in
/// <c>web/src/manifest.js</c>; the two must stay in step.
///
/// <list type="bullet">
///   <item><c>{{actor.id}}</c> / <c>{{currentUser.id}}</c> — the signed-in user. Two spellings for
///   one thing was a standing authoring trap (the renderer said one, the engine said the other), so
///   BOTH resolve everywhere now. <c>actor.id</c> is the preferred spelling.</item>
///   <item><c>{{today}}</c> — the current date as <c>yyyy-MM-dd</c>.</item>
///   <item><c>{{now}}</c> — the current instant, ISO 8601.</item>
///   <item><c>{{today+7}}</c>, <c>{{today-30d}}</c>, <c>{{today+2w}}</c>, <c>{{now-4h}}</c> — the
///   same anchors, offset. The unit defaults to days.</item>
/// </list>
///
/// <b>Units are d / w / h only, deliberately.</b> Month arithmetic diverges between JavaScript
/// (<c>setMonth</c> overflows: Jan 31 + 1m = Mar 3) and .NET (<c>AddMonths</c> clamps: Feb 28), so a
/// <c>{{today+1m}}</c> filter would quietly select different rows in the renderer than the engine.
/// Weeks are exactly 7 days on both sides, so they carry no such risk.
/// </summary>
public static class ExprTokens
{
    /// <summary>Both accepted spellings of "the signed-in user".</summary>
    public static readonly IReadOnlyList<string> ActorTokens = ["actor.id", "currentUser.id"];

    private static readonly Regex Relative =
        new(@"^(today|now)(?:\s*([+-])\s*(\d{1,4})\s*([dwh])?)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Is this the inner text of a token this grammar knows (no braces)?</summary>
    public static bool IsKnown(string inner) => Describe(inner) is null;

    /// <summary>Null when <paramref name="inner"/> is a legal token, else why it is not — so the gate
    /// can say "'h' measures hours, which a date has none of" instead of a bare "unknown token".</summary>
    public static string? Describe(string inner)
    {
        inner = inner.Trim();
        if (ActorTokens.Contains(inner)) return null;
        var m = Relative.Match(inner);
        if (!m.Success)
            return $"unknown (allowed: {string.Join(", ", ActorTokens.Select(t => "{{" + t + "}}"))}, "
                 + "{{today}}, {{now}}, and offsets like {{today+7}}, {{today-2w}}, {{now-4h}})";
        // An hour offset on a date anchor would resolve to the same day it started on: the author
        // meant {{now-4h}}, and silently agreeing with them would be the wrong kind of tolerant.
        if (m.Groups[1].Value == "today" && m.Groups[4].Value == "h")
            return "'{{today}}' is a date, so an hour offset does nothing — use '{{now}}' for hours";
        return null;
    }

    /// <summary>Resolve a token to its value, or null when it is not one. <paramref name="inner"/> is
    /// the text INSIDE the braces.</summary>
    public static string? Resolve(string inner, string actorId, DateTimeOffset now)
    {
        inner = inner.Trim();
        if (ActorTokens.Contains(inner)) return actorId;
        var m = Relative.Match(inner);
        if (!m.Success) return null;

        var anchor = m.Groups[1].Value;
        var at = anchor == "today" ? now.Date : now.UtcDateTime;
        if (m.Groups[2].Success)
        {
            var n = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            if (m.Groups[2].Value == "-") n = -n;
            at = m.Groups[4].Value switch
            {
                "w" => at.AddDays(n * 7),
                "h" => at.AddHours(n),
                _ => at.AddDays(n),
            };
        }
        return anchor == "today"
            ? at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : new DateTimeOffset(at, TimeSpan.Zero).ToString("o", CultureInfo.InvariantCulture);
    }

    /// <summary>The inner text of a <c>{{…}}</c> string, or null when it is not one.</summary>
    public static string? Inner(string s) =>
        s.Length > 4 && s.StartsWith("{{", StringComparison.Ordinal) && s.EndsWith("}}", StringComparison.Ordinal)
            ? s[2..^2].Trim()
            : null;
}

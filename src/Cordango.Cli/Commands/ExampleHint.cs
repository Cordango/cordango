// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.Definition;

namespace Cordango.Cli.Commands;

/// <summary>
/// Puts "see a working one" next to the error that needed it.
///
/// <para><b>Why here and not in the gate.</b> <c>Cordango.Compiler</c> is the portable core: it
/// states what is wrong with a document and knows nothing about commands, terminals or this
/// binary. A diagnostic that named a CLI verb would be a host concern compiled into the one
/// assembly that is supposed to have none, and it would read as nonsense from the platform, which
/// runs the same gate and has no <c>cordango</c> to run. So the gate says what is wrong, and the
/// CLI — which knows what it can offer — says where to look.</para>
///
/// <para><b>Why it is worth doing at all.</b> An agent reads the error, not the manual. Telling it
/// that <c>gantt</c> has a worked example at the moment it got a gantt wrong is the difference
/// between one correction and a loop of plausible guesses; the alternative is hoping it remembers
/// a command it read about once in <c>AGENTS.md</c>.</para>
///
/// <para>Quiet by construction: a message mentioning no known block kind gets nothing appended,
/// and a kind with no example gets nothing either. The hint appears where it can be acted on.</para>
/// </summary>
public static class ExampleHint
{
    /// <summary>The kinds worth spotting in a sentence, longest first so <c>child</c> never wins a
    /// match that belongs to a longer name.</summary>
    private static readonly IReadOnlyList<string> Kinds =
        [.. Examples.Kinds.OrderByDescending(k => k.Length)];

    /// <summary>One line of advice for a diagnostic, or null when there is nothing to add.</summary>
    public static string? For(string diagnostic)
    {
        if (string.IsNullOrWhiteSpace(diagnostic)) return null;

        foreach (var kind in Kinds)
            if (Mentions(diagnostic, kind))
                return $"see a working one: cordango example {kind}";

        return null;
    }

    /// <summary>Append the advice to each diagnostic that has any, leaving the rest alone.</summary>
    public static IEnumerable<string> Annotate(IEnumerable<string> diagnostics)
    {
        foreach (var d in diagnostics)
            yield return For(d) is { } hint ? $"{d}  ({hint})" : d;
    }

    /// <summary>A whole-word match. Without it "a row of" would advertise the `row` block and
    /// "the card" inside a sentence about something else would advertise `card` — a hint that
    /// fires on prose is worse than none, because the next one gets ignored too.</summary>
    private static bool Mentions(string text, string kind)
    {
        var at = 0;
        while ((at = text.IndexOf(kind, at, StringComparison.Ordinal)) >= 0)
        {
            var before = at == 0 ? ' ' : text[at - 1];
            var afterAt = at + kind.Length;
            var after = afterAt >= text.Length ? ' ' : text[afterAt];
            // Only where the kind is QUOTED or stands alone as a word. Gate messages name a kind
            // as `board`, 'board' or "board"; prose says "the board is".
            var quoted = before is '\'' or '"' or '`' && after is '\'' or '"' or '`';
            if (quoted) return true;
            at = afterAt;
        }
        return false;
    }
}

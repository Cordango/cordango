// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.RegularExpressions;

namespace Cordango.Cord;

/// <summary>
/// Translates the validator's errors out of App Definition pointers and into Cord paths.
///
/// <para>The gate speaks in the document it was given: <c>STRUCTURAL at [entities[3].fields[7].
/// computed.rollup.via]</c>. Under Cord the author never wrote <c>entities[3]</c>, never wrote
/// <c>computed.rollup</c>, and — for <c>via</c> especially — never wrote the property at all. Handing
/// that back unrewritten asks them to repair a document they cannot see, which is the difference
/// between a repair loop that converges and one that thrashes.</para>
///
/// <para>Indices become KEYS, because keys are what the author used:
/// <c>entities[3].fields[7]</c> → <c>entities/period/fields/payroll_cost</c>. The mechanical suffixes
/// Cord generated on their behalf collapse too, so an error about <c>computed.rollup.via</c> lands on
/// the aggregate rather than on machinery they have never heard of.</para>
///
/// <para>Best-effort by design. Anything it cannot place is passed through untouched — a pointer the
/// author does not recognise is worse than no pointer, but an error swallowed because it could not be
/// rewritten is worse than both.</para>
/// </summary>
public sealed partial class CordPointerMap
{
    public static readonly CordPointerMap Empty = new(new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>App Definition path fragment → Cord path fragment.</summary>
    private readonly IReadOnlyDictionary<string, string> _paths;

    private CordPointerMap(IReadOnlyDictionary<string, string> paths) => _paths = paths;

    public static CordPointerMap From(CordApp app, CordLowering lowering)
    {
        ArgumentNullException.ThrowIfNull(app);
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);

        var entities = app.EntityList;
        for (var e = 0; e < entities.Count; e++)
        {
            var entity = entities[e].Key;
            if (entity is null) continue;
            paths[$"entities[{e}]"] = $"entities/{entity}";

            var fields = entities[e].FieldList;
            for (var f = 0; f < fields.Count; f++)
            {
                if (fields[f].Key is not { } field) continue;
                paths[$"entities[{e}].fields[{f}]"] = $"entities/{entity}/fields/{field}";
            }
        }

        return new CordPointerMap(paths);
    }

    /// <summary>Rewrites every App Definition path in a validator message.</summary>
    public string Rewrite(string message)
    {
        if (string.IsNullOrEmpty(message) || _paths.Count == 0) return message;

        // Longest first: `entities[3].fields[7]` must win over `entities[3]`, or the field would be
        // rewritten to the entity and the suffix left dangling.
        foreach (var (from, to) in _paths.OrderByDescending(p => p.Key.Length))
            message = message.Replace(from, to, StringComparison.Ordinal);

        // The machinery Cord wrote on the author's behalf. `computed.rollup.via` is the sharpest case:
        // it names a property the semantic surface deliberately does not have, so pointing at it would
        // be pointing at nothing.
        message = ComputedRollup().Replace(message, "");
        return message.Replace(".computed.expr", "", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"\.computed\.rollup(\.\w+)*")]
    private static partial Regex ComputedRollup();
}

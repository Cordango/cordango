// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;

namespace Cordango.Integrations;

/// <summary>One step of an <see cref="ApiPath"/>: a property name, or an array index.</summary>
public sealed record ApiPathSegment(string? Name, int? Index)
{
    public override string ToString() => Name is not null ? Name : $"[{Index}]";
}

/// <summary>
/// A <c>$</c>-rooted address into a JSON value from an external API: <c>$.properties.firstname</c>,
/// <c>$.associations.contacts[0].id</c>, <c>$["odd key"]</c>.
///
/// <para><b>Deliberately not JSONPath.</b> No filters, no wildcards, no recursive descent. Every
/// path names at most one value, so a mapping is a function of the item and two implementations
/// (the platform's evaluator and, later, emitted standalone code) cannot disagree about which of
/// several matches was meant. Lists are taken only where an operation's <c>items</c> says so.</para>
///
/// <para>This is the parser. Reading a value with it is execution and lives with whatever executes —
/// the same split as the expression grammar.</para>
/// </summary>
public sealed record ApiPath(string Text, IReadOnlyList<ApiPathSegment> Segments)
{
    /// <summary>True for <c>$</c> alone: the whole value.</summary>
    public bool IsRoot => Segments.Count == 0;

    public static ApiPath? TryParse(string? text, out string? error)
    {
        error = null;
        if (string.IsNullOrEmpty(text) || text[0] != '$')
        {
            error = $"'{text}' is not a path — a path starts with '$' ($.a.b, $.items[0], $[\"odd key\"])";
            return null;
        }

        var segments = new List<ApiPathSegment>();
        var i = 1;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '.')
            {
                var start = ++i;
                while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] is '_' or '-')) i++;
                if (i == start)
                {
                    error = $"'{text}': a '.' must be followed by a property name (use $[\"…\"] for a name with other characters)";
                    return null;
                }
                segments.Add(new ApiPathSegment(text[start..i], null));
            }
            else if (c == '[')
            {
                i++;
                if (i < text.Length && text[i] == '"')
                {
                    var name = new StringBuilder();
                    i++;
                    var closed = false;
                    while (i < text.Length)
                    {
                        if (text[i] == '\\' && i + 1 < text.Length) { name.Append(text[i + 1]); i += 2; continue; }
                        if (text[i] == '"') { closed = true; i++; break; }
                        name.Append(text[i++]);
                    }
                    if (!closed || i >= text.Length || text[i] != ']' || name.Length == 0)
                    {
                        error = $"'{text}': a quoted name is written $[\"name\"]";
                        return null;
                    }
                    i++;
                    segments.Add(new ApiPathSegment(name.ToString(), null));
                }
                else
                {
                    var start = i;
                    while (i < text.Length && char.IsAsciiDigit(text[i])) i++;
                    if (i == start || i >= text.Length || text[i] != ']' || i - start > 6)
                    {
                        error = $"'{text}': an index is a whole number in brackets ([0], [3]) — filters and wildcards are not paths";
                        return null;
                    }
                    segments.Add(new ApiPathSegment(null, int.Parse(text[start..i], System.Globalization.CultureInfo.InvariantCulture)));
                    i++;
                }
            }
            else
            {
                error = $"'{text}': unexpected '{c}' — a path continues with .name, [index] or [\"name\"]";
                return null;
            }
        }

        if (segments.Count > 16)
        {
            error = $"'{text}' is {segments.Count} steps deep; 16 is the limit";
            return null;
        }
        return new ApiPath(text, segments);
    }
}

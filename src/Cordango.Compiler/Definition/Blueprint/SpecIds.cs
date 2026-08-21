// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;

namespace Cordango.Definition.Blueprints;

/// <summary>
/// Mints the ids that more than one interview pass has to agree on.
///
/// <para><b>Why these are derived rather than proposed.</b> A field is already identified by the
/// concept it sits on and its key — that pair is unique, and the gate enforces it. Its <c>id</c> is a
/// third, independent name for the same thing, and THREE passes have to invent it identically: the
/// concept pass names the field that titles a record, the workflow pass names the field a transition
/// requires, and the data pass creates it. Each runs as its own model call, none can see what the
/// others will choose, and every disagreement surfaces as a gate error about a field that "belongs to
/// another concept" or "does not exist".</para>
///
/// <para>No amount of prompt wording fixes that — it is three independent guesses that must coincide.
/// Deriving the id from (concept, key) makes them coincide by construction: a proposer says WHICH
/// FIELD it means in the only terms it can be sure of, and code does the naming. A field id also
/// cannot then belong to another concept, because the owning concept is literally inside it.</para>
/// </summary>
public static class SpecIds
{
    public static string Field(string conceptKey, string fieldKey) =>
        $"fld_{Slug(conceptKey)}_{Slug(fieldKey)}";

    /// <summary>Ids match <c>[a-z0-9_]+</c>, so anything else becomes an underscore. Runs of them
    /// collapse: 'Cost / Type' and 'Cost - Type' are the same field asked for twice, and two ids
    /// would make them two columns.</summary>
    internal static string Slug(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '_') sb.Append('_');
        }
        while (sb.Length > 0 && sb[^1] == '_') sb.Length--;
        return sb.Length > 0 ? sb.ToString() : "x";
    }
}

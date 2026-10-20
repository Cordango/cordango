// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.RegularExpressions;

namespace Cordango.Integrations;

/// <summary>
/// The <c>{{variable}}</c> placeholders in a provider's URLs, query values, headers and bodies.
///
/// <para><b>What a template may read is fixed by where it sits</b>, and a credential is never one of
/// them. The auth block places a secret into a request; a template that could name one would let a
/// provider definition copy it into a query string, a body or a log line, and the gate could no
/// longer say where a secret goes. Every root outside the allowed set is refused by name.</para>
/// </summary>
public static partial class RequestTemplate
{
    /// <summary>Variables a request may read, by place. <c>config.*</c> means any declared config key.</summary>
    public static class Scope
    {
        public static readonly IReadOnlySet<string> Url = Set("config.*");
        public static readonly IReadOnlySet<string> GetPath = Set("config.*", "id");
        public static readonly IReadOnlySet<string> Query = Set("config.*", "page.size", "page.cursor", "page.offset", "page.number", "search.text");
        public static readonly IReadOnlySet<string> Body = Set("config.*", "page.size", "page.cursor", "page.offset", "page.number", "search.text", "filters", "sorts");
        public static readonly IReadOnlySet<string> Filter = Set("filter.name", "filter.op", "filter.value");
        public static readonly IReadOnlySet<string> Sort = Set("sort.name", "sort.direction");

        private static HashSet<string> Set(params string[] names) => new(names, StringComparer.Ordinal);
    }

    [GeneratedRegex(@"\{\{(.*?)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"^[a-z][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex VariableName();

    /// <summary>The variable names in <paramref name="text"/>, trimmed. A malformed placeholder is
    /// reported through <paramref name="error"/> and the list stops there.</summary>
    public static IReadOnlyList<string> Variables(string text, out string? error)
    {
        error = null;
        var found = new List<string>();
        foreach (Match m in Placeholder().Matches(text))
        {
            var name = m.Groups[1].Value.Trim();
            if (name.Length == 0)
            {
                error = $"'{text}' has an empty {{{{}}}}";
                return found;
            }
            found.Add(name);
        }
        var stripped = Placeholder().Replace(text, "");
        if (stripped.Contains("{{", StringComparison.Ordinal) || stripped.Contains("}}", StringComparison.Ordinal))
            error = $"'{text}' has an unclosed {{{{ or a stray }}}}";
        return found;
    }

    /// <summary>True when the whole string is one placeholder. Such a value is replaced keeping the
    /// variable's JSON type — a page size stays a number, a filter list stays an array.</summary>
    public static bool IsWhole(string text, out string? variable)
    {
        variable = null;
        var m = Placeholder().Match(text);
        if (!m.Success || m.Index != 0 || m.Length != text.Length) return false;
        variable = m.Groups[1].Value.Trim();
        return true;
    }

    /// <summary>Checks every placeholder in <paramref name="text"/> against <paramref name="allowed"/>
    /// and the declared config keys, adding one error per problem.</summary>
    public static void Check(string text, IReadOnlySet<string> allowed, IReadOnlySet<string> configKeys,
        string where, List<string> errors)
    {
        var names = Variables(text, out var error);
        if (error is not null) errors.Add($"SEMANTIC: {where}: {error}");
        foreach (var name in names)
        {
            if (!VariableName().IsMatch(name))
            {
                errors.Add($"SEMANTIC: {where}: '{{{{{name}}}}}' is not a variable name");
                continue;
            }
            if (name.StartsWith("config.", StringComparison.Ordinal) && allowed.Contains("config.*"))
            {
                var key = name["config.".Length..];
                if (!configKeys.Contains(key))
                    errors.Add($"SEMANTIC: {where}: '{{{{{name}}}}}' names config '{key}', which is not declared"
                             + (configKeys.Count > 0 ? $" (declared: {string.Join(", ", configKeys.Order(StringComparer.Ordinal))})" : ""));
                continue;
            }
            if (allowed.Contains(name)) continue;
            errors.Add($"SEMANTIC: {where}: '{{{{{name}}}}}' is not a variable this part of a request may use "
                     + $"(allowed here: {string.Join(", ", allowed.Order(StringComparer.Ordinal).Select(a => a == "config.*" ? "config.<key>" : a))}). "
                     + "A credential is never a template variable: the auth block places it");
        }
    }
}

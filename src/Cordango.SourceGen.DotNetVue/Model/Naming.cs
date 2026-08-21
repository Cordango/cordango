// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;

namespace Cordango.SourceGen.DotNetVue.Model;

/// <summary>
/// Turning definition keys into C# and SQL names.
///
/// <para>Every rule here is total and reversible-by-eye: <c>expense_claim</c> becomes
/// <c>ExpenseClaim</c> and the table stays <c>expense_claim</c>, so somebody reading a stack trace,
/// a query log and the definition side by side sees the same thing three times. Nothing is
/// abbreviated, nothing is pluralised, and nothing is prettified — a generator that improved names
/// would break that correspondence for a cosmetic gain.</para>
/// </summary>
public static class Naming
{
    /// <summary>A key as a type or property name: <c>expense_claim</c> → <c>ExpenseClaim</c>.</summary>
    public static string Pascal(string key)
    {
        if (string.IsNullOrEmpty(key)) return "Unnamed";

        var builder = new StringBuilder(key.Length);
        var upper = true;

        foreach (var c in key)
        {
            if (c is '_' or '-' or ' ' or '.') { upper = true; continue; }
            builder.Append(upper ? char.ToUpperInvariant(c) : c);
            upper = false;
        }

        var name = builder.ToString();
        if (name.Length == 0) return "Unnamed";

        // A key may legitimately start with a digit — "2fa_enabled" is a field somebody will write —
        // and an identifier may not.
        if (char.IsDigit(name[0])) name = "N" + name;
        return name;
    }

    /// <summary>A key as a JavaScript identifier: <c>expense_claim</c> → <c>expenseClaim</c>.</summary>
    public static string Camel(string key)
    {
        var pascal = Pascal(key);
        return char.ToLowerInvariant(pascal[0]) + pascal[1..];
    }

    /// <summary>
    /// A property name that cannot collide with the type it lives on.
    ///
    /// <para>C# forbids a member with the same name as its enclosing type — an entity <c>invoice</c>
    /// with a field <c>invoice</c> is perfectly legal in a definition and does not compile as a
    /// class. The suffix is applied only when it is needed, so the ordinary case reads normally.</para>
    /// </summary>
    public static string Property(string fieldKey, string entityKey)
    {
        var name = Pascal(fieldKey);
        return name == Pascal(entityKey) ? name + "Value" : name;
    }

    /// <summary>The table an entity's rows live in — the definition's key, unchanged. Postgres
    /// reserved words are not a problem: EF quotes every identifier it emits.</summary>
    public static string Table(string entityKey) => Sanitise(entityKey);

    /// <summary>The column a field lives in — the definition's key, unchanged, so a query log reads
    /// like the definition.</summary>
    public static string Column(string fieldKey) => Sanitise(fieldKey);

    /// <summary>Lower-case, <c>[a-z0-9_]</c> only, never starting with a digit. Matches what the
    /// platform's own data plane does, so the same definition produces the same column names in both
    /// products and a database can be moved between them.</summary>
    public static string Sanitise(string raw)
    {
        var builder = new StringBuilder(raw.Length);
        foreach (var c in raw.ToLowerInvariant())
            builder.Append(c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' ? c : '_');

        var name = builder.ToString();
        if (name.Length == 0) return "unnamed";
        if (char.IsDigit(name[0])) name = "n" + name;
        return name;
    }

    /// <summary>A C# string literal, quotes and backslashes escaped. Used everywhere a definition
    /// value ends up in generated source, because definition values are written by people and
    /// contain apostrophes, quotes and newlines.</summary>
    public static string Literal(string? value)
    {
        if (value is null) return "null";

        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default: builder.Append(c); break;
            }
        }
        builder.Append('"');
        return builder.ToString();
    }

    /// <summary>A JavaScript string literal. Same escapes plus the one that matters in a Vue
    /// template: a closing script tag inside a string ends the script block.</summary>
    public static string JsLiteral(string? value) =>
        value is null ? "null" : Literal(value).Replace("</", "<\\/", StringComparison.Ordinal);
}

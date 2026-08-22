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
    /// The C# class name for an entity's records — <c>Pascal(key)</c>, unless that name is already
    /// taken in the files that will use it.
    ///
    /// <para><b>Two collisions, both found by generating a second application.</b> An entity keyed
    /// <c>time_off</c> in an application keyed <c>time-off</c> produces a class <c>TimeOff</c> in
    /// namespace <c>TimeOff.Entities</c>, and every file that says <c>TimeOff</c> resolves it to the
    /// NAMESPACE: <i>"TimeOff is a namespace but is used like a type"</i>, at fourteen sites. An
    /// entity keyed <c>task</c> produces a class <c>Task</c>, and a file that imports both it and
    /// <c>System.Threading.Tasks</c> — which every generated file does, implicitly — cannot say
    /// <c>Task</c> at all.</para>
    ///
    /// <para>Neither is a strange thing to call something. "Task" is the most ordinary noun in
    /// project management, and the toolchain has no business refusing it or quietly producing an
    /// application that will not compile. So the class is suffixed, and only when it has to be:
    /// <c>TaskRecord</c>, <c>TimeOffRecord</c>. The same rule <see cref="Property"/> already applies
    /// when a field is named after its own entity, for the same reason.</para>
    ///
    /// <para><b>The list below is what generated files can see</b>, not everything in .NET. It
    /// covers the implicit usings of a web project plus the runtime namespaces the emitters import,
    /// and a name outside it is one no generated file mentions. If one is ever missed the failure is
    /// loud and immediate — the generated application does not compile — which is the right failure
    /// mode for a guess, and <c>GeneratedApplicationTests</c> compiles every corpus application to
    /// keep the guess honest.</para>
    /// </summary>
    /// <param name="entityKey">The entity's key, as the definition spells it.</param>
    /// <param name="appNamespace">The application's root namespace, which a type cannot share a name
    /// with, along with the sub-namespaces the generator puts files in.</param>
    public static string Type(string entityKey, string? appNamespace = null)
    {
        var name = Pascal(entityKey);
        return IsTaken(name, appNamespace) ? name + "Record" : name;
    }

    private static bool IsTaken(string name, string? appNamespace) =>
        name == appNamespace
        || SubNamespaces.Contains(name)
        || Reserved.Contains(name);

    /// <summary>The namespaces the generator puts files in. A class with one of these names is
    /// ambiguous with the namespace from anywhere inside the application.</summary>
    private static readonly HashSet<string> SubNamespaces = new(StringComparer.Ordinal)
    {
        "Commands", "Controllers", "Data", "Entities", "Hooks", "Identity", "Migrations", "Security", "Seed",
    };

    /// <summary>
    /// Type names already in scope in generated files.
    ///
    /// <para>The implicit usings of <c>Microsoft.NET.Sdk.Web</c> (<c>System</c>,
    /// <c>System.Collections.Generic</c>, <c>System.IO</c>, <c>System.Linq</c>,
    /// <c>System.Net.Http</c>, <c>System.Threading</c>, <c>System.Threading.Tasks</c>, and the
    /// <c>Microsoft.AspNetCore.*</c> / <c>Microsoft.Extensions.*</c> set), plus what the emitters
    /// import by hand: <c>Microsoft.EntityFrameworkCore</c>, <c>Microsoft.AspNetCore.Mvc</c>,
    /// <c>System.Text.Json</c>, and the Cordango runtime — whose directory types (<c>Person</c>,
    /// <c>Department</c>, <c>Organization</c>) are the ones an application is most likely to want to
    /// name an entity after.</para>
    ///
    /// <para>Only names a business would plausibly use for an entity are listed. <c>Int32</c> is in
    /// scope too and is not here, because an entity called "int32" is not a thing that happens.</para>
    /// </summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        // System
        "Action", "Activator", "Array", "Attribute", "Buffer", "Comparer", "Console", "Convert",
        "DateOnly", "DateTime", "DateTimeOffset", "Delegate", "Enum", "Environment", "Exception",
        "Guid", "Index", "Lazy", "Math", "Memory", "Nullable", "Object", "Random", "Range",
        "Span", "String", "TimeOnly", "TimeSpan", "TimeZone", "Tuple", "Type", "Uri", "Version",

        // System.Collections.Generic
        "Dictionary", "HashSet", "KeyValuePair", "LinkedList", "List", "Queue", "SortedList", "Stack",

        // System.IO
        "Directory", "File", "Path", "Stream",

        // System.Threading and System.Threading.Tasks
        "Task", "Timer",

        // System.Text.Json, Microsoft.EntityFrameworkCore, Microsoft.AspNetCore.Mvc
        "Index", "JsonDocument", "JsonElement", "ModelBuilder", "Route",

        // The Cordango runtime, as generated files import it.
        "Contact", "Department", "Group", "Notification", "Organization", "Person",
        "Record", "RecordContext", "RecordDescriptor", "RecordException",
    };

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

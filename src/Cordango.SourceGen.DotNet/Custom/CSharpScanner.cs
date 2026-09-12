// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Globalization;
using System.Text.Json.Nodes;
using Cordango.SourceGen.Common;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cordango.SourceGen.DotNet.Custom;

/// <summary>
/// Reads the C# under <c>custom/dotnet/</c> and answers with the contract the compiler type-checks
/// expressions against.
///
/// <para><b>Syntax only.</b> It parses; it never builds a compilation. So it cannot resolve a
/// <c>using</c>, cannot follow an alias and cannot see into another file — and the rules are shaped
/// so that it does not have to. The types it accepts are three exact spellings, and anything else
/// is refused by the text of the declaration with a message naming the three.</para>
///
/// <para><b>It is not the last word and does not pretend to be.</b> Everything it reads becomes a
/// generated call site that the real C# compiler then compiles. A signature this misreads fails
/// <c>dotnet build</c> in the generated application, with a file and a line — which is why being
/// strict and simple here beats being clever.</para>
/// </summary>
public sealed class CSharpScanner : ICustomCodeScanner
{
    public string Language => "dotnet";

    public string SourceExtension => ".cs";

    /// <summary>The three types a computed expression can carry, and their Cord kinds. Closed, and
    /// every entry nullable: a computed value can be unknown — a division by zero, a blank column,
    /// a branch that could not be worked out — and a non-nullable parameter has nowhere to put
    /// that.</summary>
    private static readonly IReadOnlyDictionary<string, string> Kinds =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["decimal?"] = "number",
            ["bool?"] = "boolean",
            ["string?"] = "text",
        };

    /// <summary>Members whose value differs the next time you ask. A computed field is worked out
    /// when the row is written and stored beside it, so one of these makes the stored figure
    /// disagree with the expression that claims to produce it.</summary>
    private static readonly IReadOnlySet<string> Nondeterministic =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "DateTime.Now", "DateTime.UtcNow", "DateTime.Today",
            "DateTimeOffset.Now", "DateTimeOffset.UtcNow",
            "Guid.NewGuid", "Random.Shared", "Environment.TickCount",
        };

    /// <summary>Reaching outside the function. A warning rather than an error: legitimate in a
    /// hook, and worth a second look in something a formula calls.</summary>
    private static readonly IReadOnlySet<string> Outward =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "File", "Directory", "Console", "Environment", "HttpClient", "Process",
        };

    /// <summary>The hook attributes and what each adapts to. The two before-hooks are the only ones
    /// with a stage, because the rest run once the write is already decided.</summary>
    private static readonly IReadOnlyDictionary<string, HookShape> Hooks =
        new Dictionary<string, HookShape>(StringComparer.Ordinal)
        {
            ["BeforeCreate"] = new("before_create", Paired: false, Staged: true),
            ["AfterCreate"] = new("after_create", Paired: false, Staged: false),
            ["BeforeUpdate"] = new("before_update", Paired: true, Staged: true),
            ["AfterUpdate"] = new("after_update", Paired: true, Staged: false),
            ["BeforeDelete"] = new("before_delete", Paired: false, Staged: false),
            ["AfterDelete"] = new("after_delete", Paired: false, Staged: false),
        };

    private sealed record HookShape(string Event, bool Paired, bool Staged);

    public CustomScanResult Scan(CustomCodeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var appNamespace = Naming.Pascal(context.AppKey);
        var expected = appNamespace + ".Custom";

        // Keyed BY generated type name, which is what a hook's first parameter is written as. Built
        // with the emitters' own naming rather than by taking a name apart.
        var entities = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entity in context.Entities)
            entities[Naming.Type(entity.Key, appNamespace)] = entity.Key;

        var errors = new List<Diagnostic>();
        var warnings = new List<Diagnostic>();
        var functions = new List<(string Sort, JsonObject Json)>();
        var hooks = new List<(string Sort, JsonObject Json)>();
        var claimed = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var source in context.Sources.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            var path = source.Key;
            var root = CSharpSyntaxTree.ParseText(source.Value, path: path).GetCompilationUnitRoot();

            foreach (var broken in root.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error))
                errors.Add(At(path, broken.Location, CustomCodeCodes.Shape,
                    "this file does not parse as C#: " + broken.GetMessage()));

            if (root.ContainsDirectives)
            {
                errors.Add(At(path, null, CustomCodeCodes.Shape,
                    "this file uses a preprocessor directive. Signatures are read from the text of "
                    + "the file without compiling it, so a declaration that exists under one #if "
                    + "and not another cannot be read at all. Move the condition inside a method "
                    + "body, or split the file."));
                continue;
            }

            var declared = Namespace(root);
            if (!string.Equals(declared, expected, StringComparison.Ordinal))
            {
                errors.Add(At(path, null, CustomCodeCodes.Shape,
                    "this file declares namespace " + (declared ?? "nothing") + ". Custom code "
                    + "lives in " + expected + ", so generated code can call it without guessing "
                    + "where it is."));
                continue;
            }

            foreach (var type in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                var isFunctions = AttributeNamed(type.AttributeLists, "CordangoFunctions") is not null;
                var isHooks = AttributeNamed(type.AttributeLists, "CordangoHooks") is not null;

                if (!isFunctions && !isHooks)
                {
                    Orphans(path, type, errors);
                    continue;
                }

                if (!Shaped(type, path, isFunctions, errors)) continue;

                foreach (var method in type.Members.OfType<MethodDeclarationSyntax>())
                {
                    if (isFunctions && AttributeNamed(method.AttributeLists, "CordangoFunction") is { } marked)
                        ReadFunction(path, type, method, marked, claimed, functions, errors, warnings);

                    if (!isHooks) continue;

                    foreach (var hook in Hooks)
                    {
                        if (AttributeNamed(method.AttributeLists, hook.Key) is not { } applied) continue;
                        ReadHook(path, type, method, applied, hook.Key, hook.Value, entities, hooks, errors);
                    }
                }
            }
        }

        var metadata = new JsonObject { ["language"] = Language };
        if (functions.Count > 0) metadata["functions"] = Ordered(functions);
        if (hooks.Count > 0) metadata["hooks"] = Ordered(hooks);

        return new CustomScanResult(metadata, errors, warnings);
    }

    private static JsonArray Ordered(List<(string Sort, JsonObject Json)> rows) =>
        new([.. rows.OrderBy(r => r.Sort, StringComparer.Ordinal).Select(r => (JsonNode)r.Json)]);

    /// <summary>A helper class is ordinary C# and is left alone. An attributed METHOD inside one is
    /// a mistake that would otherwise be silence — the marker is what makes a file say what it is
    /// from its first line, and a method that asked to be called and never is reads as our bug.
    /// </summary>
    private static void Orphans(string path, ClassDeclarationSyntax type, List<Diagnostic> errors)
    {
        foreach (var method in type.Members.OfType<MethodDeclarationSyntax>())
        {
            var marker = AttributeNamed(method.AttributeLists, "CordangoFunction") is not null
                ? "[CordangoFunctions]"
                : Hooks.Keys.Any(h => AttributeNamed(method.AttributeLists, h) is not null)
                    ? "[CordangoHooks]"
                    : null;

            if (marker is null) continue;

            errors.Add(At(path, method.Identifier.GetLocation(), CustomCodeCodes.Shape,
                method.Identifier.ValueText + " is marked for Cordango, but "
                + type.Identifier.ValueText + " does not carry " + marker
                + ", so nothing would look inside it."));
        }
    }

    /// <summary>The rules a marked CLASS satisfies before its members are worth reading.</summary>
    private static bool Shaped(
        ClassDeclarationSyntax type, string path, bool isFunctions, List<Diagnostic> errors)
    {
        var where = type.Identifier.GetLocation();
        var label = type.Identifier.ValueText;
        var ok = true;

        if (!type.Modifiers.Any(SyntaxKind.PublicKeyword))
        {
            errors.Add(At(path, where, CustomCodeCodes.Shape,
                label + " must be public. Generated code in another file calls into it."));
            ok = false;
        }

        if (type.TypeParameterList is not null)
        {
            errors.Add(At(path, where, CustomCodeCodes.Shape, label + " must not be generic."));
            ok = false;
        }

        if (type.Parent is not (FileScopedNamespaceDeclarationSyntax or NamespaceDeclarationSyntax))
        {
            errors.Add(At(path, where, CustomCodeCodes.Shape,
                label + " must be a top-level class, not nested inside another type."));
            ok = false;
        }

        if (isFunctions && !type.Modifiers.Any(SyntaxKind.StaticKeyword))
        {
            errors.Add(At(path, where, CustomCodeCodes.Shape,
                label + " holds custom functions, which are called with no instance, so it must be "
                + "static."));
            ok = false;
        }

        return ok;
    }

    private void ReadFunction(
        string path, ClassDeclarationSyntax type, MethodDeclarationSyntax method,
        AttributeSyntax attribute, Dictionary<string, string> claimed,
        List<(string, JsonObject)> functions, List<Diagnostic> errors, List<Diagnostic> warnings)
    {
        var where = method.Identifier.GetLocation();
        var name = Literal(attribute, 0);

        if (name is null)
        {
            errors.Add(At(path, where, CustomCodeCodes.Shape,
                method.Identifier.ValueText + " does not say what an expression should call it. "
                + "Give CordangoFunction a literal name."));
            return;
        }

        if (!IsCordName(name))
        {
            errors.Add(At(path, where, CustomCodeCodes.Shape,
                name + " is not a name an expression can call. Use lower case letters, digits and "
                + "underscores, starting with a letter — the shape every other key in a definition "
                + "has."));
            return;
        }

        if (claimed.TryGetValue(name, out var first))
        {
            errors.Add(At(path, where, CustomCodeCodes.Duplicate,
                "custom." + name + " is already declared by " + first + ". Two declarations of one "
                + "name would give an expression two answers."));
            return;
        }

        var ok = true;

        if (!method.Modifiers.Any(SyntaxKind.PublicKeyword) || !method.Modifiers.Any(SyntaxKind.StaticKeyword))
        {
            errors.Add(At(path, where, CustomCodeCodes.Shape,
                name + " must be a public static method. Generated code calls it directly, with no "
                + "instance to call it on."));
            ok = false;
        }

        if (method.Modifiers.Any(SyntaxKind.AsyncKeyword) || Awaitable(method.ReturnType))
        {
            errors.Add(At(path, where, CustomCodeCodes.Determinism,
                name + " is asynchronous. A computed field is worked out in the middle of a write, "
                + "many times over during a recompute, so a function a formula calls has to answer "
                + "immediately. Do the waiting in a hook instead."));
            ok = false;
        }

        if (method.TypeParameterList is not null)
        {
            errors.Add(At(path, where, CustomCodeCodes.Shape,
                name + " is generic. An expression has no way to say which type it meant."));
            ok = false;
        }

        var returns = Kind(method.ReturnType);
        if (returns is null)
        {
            errors.Add(At(path, where, CustomCodeCodes.Type, Unwritable(name, method.ReturnType, "return type")));
            ok = false;
        }

        var parameters = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var parameter in method.ParameterList.Parameters)
        {
            var pn = parameter.Identifier.ValueText;

            if (parameter.Modifiers.Count > 0 || parameter.Default is not null)
            {
                errors.Add(At(path, where, CustomCodeCodes.Shape,
                    "parameter " + pn + " of " + name + " uses a modifier or a default. An "
                    + "expression passes its arguments positionally and by value, and nothing else."));
                ok = false;
                continue;
            }

            var kind = Kind(parameter.Type);
            if (kind is null)
            {
                errors.Add(At(path, where, CustomCodeCodes.Type,
                    Unwritable(name, parameter.Type, "parameter " + pn)));
                ok = false;
                continue;
            }

            if (!seen.Add(pn))
            {
                errors.Add(At(path, where, CustomCodeCodes.Shape,
                    name + " has two parameters called " + pn + "."));
                ok = false;
                continue;
            }

            parameters.Add(new JsonObject { ["name"] = pn, ["kind"] = kind });
        }

        foreach (var problem in Behaviour(path, name, method, warnings))
        {
            errors.Add(problem);
            ok = false;
        }

        if (!ok) return;

        claimed[name] = path + " (" + type.Identifier.ValueText + "." + method.Identifier.ValueText + ")";

        var json = new JsonObject
        {
            ["name"] = name,
            ["returns"] = returns,
            ["params"] = parameters,
            ["source"] = Source(path, type, method),
        };

        if (Literal(attribute, "Description") is { } description) json["description"] = description;

        functions.Add((Sort(path, method), json));
    }

    private static void ReadHook(
        string path, ClassDeclarationSyntax type, MethodDeclarationSyntax method,
        AttributeSyntax attribute, string attributeName, HookShape shape,
        IReadOnlyDictionary<string, string> entities,
        List<(string, JsonObject)> hooks, List<Diagnostic> errors)
    {
        var where = method.Identifier.GetLocation();
        var label = method.Identifier.ValueText;

        if (!method.Modifiers.Any(SyntaxKind.PublicKeyword) || method.Modifiers.Any(SyntaxKind.StaticKeyword))
        {
            errors.Add(At(path, where, CustomCodeCodes.Shape,
                label + " must be a public instance method. The class is resolved from the "
                + "container so it can take its dependencies through its constructor."));
            return;
        }

        if (method.ReturnType.ToString() is not ("Task" or "System.Threading.Tasks.Task"))
        {
            errors.Add(At(path, where, CustomCodeCodes.HookSignature,
                label + " returns " + method.ReturnType + ". A hook returns Task."));
            return;
        }

        var parameters = method.ParameterList.Parameters;
        var wanted = shape.Paired ? 4 : 3;

        if (parameters.Count != wanted)
        {
            errors.Add(At(path, where, CustomCodeCodes.HookSignature, Signature(label, attributeName, shape)));
            return;
        }

        var entityType = parameters[0].Type?.ToString();
        if (entityType is null || !entities.TryGetValue(entityType, out var entityKey))
        {
            errors.Add(At(path, where, CustomCodeCodes.HookSignature,
                label + " takes " + (entityType ?? "nothing") + " first, which is not one of this "
                + "application's records. A hook's first parameter is the record it runs for."));
            return;
        }

        if (shape.Paired && parameters[1].Type?.ToString() != entityType)
        {
            errors.Add(At(path, where, CustomCodeCodes.HookSignature,
                label + " runs on an update, which hands over BOTH the incoming record and the row "
                + "as it was, so its second parameter is another " + entityType + ". "
                + Signature(label, attributeName, shape)));
            return;
        }

        if (parameters[wanted - 2].Type?.ToString() != "RecordContext"
            || parameters[wanted - 1].Type?.ToString() != "CancellationToken")
        {
            errors.Add(At(path, where, CustomCodeCodes.HookSignature, Signature(label, attributeName, shape)));
            return;
        }

        var json = new JsonObject
        {
            ["entity"] = entityKey,
            ["event"] = shape.Event,
            ["source"] = Source(path, type, method),
        };

        if (shape.Staged)
            json["stage"] = Member(attribute, "Stage") is "AfterComputed" ? "after_computed" : "before_computed";

        hooks.Add((Sort(path, method), json));
    }

    /// <summary>What a body does that a formula must not, and what merely deserves a second look.
    /// Walked as syntax rather than matched as text: a comment mentioning <c>DateTime.UtcNow</c> is
    /// not a call, and <c>System.DateTime.UtcNow</c> is.</summary>
    private static IEnumerable<Diagnostic> Behaviour(
        string path, string name, MethodDeclarationSyntax method, List<Diagnostic> warnings)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        var outward = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in method.DescendantNodes())
        {
            if (node is MemberAccessExpressionSyntax access)
            {
                var whole = access.ToString();
                foreach (var member in Nondeterministic)
                    if (whole.EndsWith(member, StringComparison.Ordinal)) found.Add(member);

                if (access.Expression is IdentifierNameSyntax id && Outward.Contains(id.Identifier.ValueText))
                    outward.Add(id.Identifier.ValueText);
            }
            else if (node is ObjectCreationExpressionSyntax creation)
            {
                var created = creation.Type.ToString();
                if (created.EndsWith("Random", StringComparison.Ordinal)) found.Add("new Random");
                if (Outward.Contains(created)) outward.Add(created);
            }
        }

        foreach (var member in outward.OrderBy(o => o, StringComparer.Ordinal))
            warnings.Add(At(path, method.Identifier.GetLocation(), CustomCodeCodes.SideEffect,
                name + " mentions " + member + ". A function a formula calls should work its answer "
                + "out from its arguments alone — anything it reaches for is read again on every "
                + "recompute, and is not part of the record."));

        foreach (var member in found.OrderBy(f => f, StringComparer.Ordinal))
            yield return At(path, method.Identifier.GetLocation(), CustomCodeCodes.Determinism,
                name + " reads " + member + ". A computed figure is worked out when the row is "
                + "written and stored beside it, so one that depends on the clock or on chance is "
                + "right once and wrong afterwards, with nothing on the surface to say so. Pass "
                + "what you need in as an argument — a date the record already holds.");
    }

    private static string Signature(string label, string attribute, HookShape shape) =>
        shape.Paired
            ? label + " is marked " + attribute
                + ", which runs as: Task M(T record, T before, RecordContext ctx, CancellationToken ct)"
            : label + " is marked " + attribute
                + ", which runs as: Task M(T record, RecordContext ctx, CancellationToken ct)";

    private static string Unwritable(string name, TypeSyntax? type, string what) =>
        "the " + what + " of " + name + " is " + (type?.ToString() ?? "nothing")
        + ", which a formula cannot carry. A computed value can be unknown, so every one is "
        + "nullable, and numbers are decimal throughout — an average of integers is not an integer. "
        + "Use decimal?, bool? or string?. A date is not offered yet.";

    private static string? Kind(TypeSyntax? type) =>
        type is not null && Kinds.TryGetValue(type.ToString(), out var kind) ? kind : null;

    private static bool Awaitable(TypeSyntax type)
    {
        var text = type.ToString();
        return text.StartsWith("Task", StringComparison.Ordinal)
            || text.StartsWith("ValueTask", StringComparison.Ordinal);
    }

    private static JsonObject Source(string path, ClassDeclarationSyntax type, MethodDeclarationSyntax method) =>
        new()
        {
            ["file"] = path,
            ["type"] = type.Identifier.ValueText,
            ["method"] = method.Identifier.ValueText,
        };

    /// <summary>File order, then declaration order within it. Registration order is RUN order for
    /// hooks, so it has to be the order somebody reading the source would expect.</summary>
    private static string Sort(string path, MethodDeclarationSyntax method) =>
        path + " " + method.SpanStart.ToString("D9", CultureInfo.InvariantCulture);

    private static string? Namespace(CompilationUnitSyntax root) =>
        root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();

    /// <summary>An attribute by name, written either way round: C# lets <c>[CordangoFunction]</c>
    /// and <c>[CordangoFunctionAttribute]</c> mean the same thing, and somebody will write both.
    /// </summary>
    private static AttributeSyntax? AttributeNamed(SyntaxList<AttributeListSyntax> lists, string name)
    {
        foreach (var list in lists)
            foreach (var attribute in list.Attributes)
            {
                var written = attribute.Name.ToString();
                var last = written[(written.LastIndexOf('.') + 1)..];
                if (string.Equals(last, name, StringComparison.Ordinal)
                    || string.Equals(last, name + "Attribute", StringComparison.Ordinal))
                {
                    return attribute;
                }
            }

        return null;
    }

    private static string? Literal(AttributeSyntax attribute, int position) =>
        Text(attribute.ArgumentList?.Arguments.Where(a => a.NameEquals is null).ElementAtOrDefault(position));

    private static string? Literal(AttributeSyntax attribute, string member) =>
        Text(attribute.ArgumentList?.Arguments
            .FirstOrDefault(a => a.NameEquals?.Name.Identifier.ValueText == member));

    private static string? Text(AttributeArgumentSyntax? argument) =>
        argument?.Expression is LiteralExpressionSyntax literal
        && literal.IsKind(SyntaxKind.StringLiteralExpression)
            ? literal.Token.ValueText
            : null;

    private static string? Member(AttributeSyntax attribute, string member)
    {
        var argument = attribute.ArgumentList?.Arguments
            .FirstOrDefault(a => a.NameEquals?.Name.Identifier.ValueText == member);
        if (argument?.Expression is null) return null;

        var written = argument.Expression.ToString();
        return written[(written.LastIndexOf('.') + 1)..];
    }

    private static bool IsCordName(string name) =>
        name.Length > 0 && char.IsAsciiLetterLower(name[0])
        && name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_');

    private static Diagnostic At(string path, Location? location, string code, string message)
    {
        var line = location is null || location == Location.None
            ? null
            : (int?)(location.GetLineSpan().StartLinePosition.Line + 1);

        return new Diagnostic(code, message,
            line is null ? path : path + ":" + line.Value.ToString(CultureInfo.InvariantCulture));
    }
}

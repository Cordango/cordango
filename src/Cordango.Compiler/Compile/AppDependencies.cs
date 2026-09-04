// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Compile;

/// <summary>
/// Something worth telling the author that is not a refusal.
///
/// <para>The pipeline used to have two channels: errors (the document is wrong) and fills (a
/// deterministic completion happened). Neither fits "you reference an app you never declared" — it
/// is legal, it compiles, and it is still the thing the author most wants to know. So a diagnostic
/// carries its own <see cref="Severity"/>, a stable <see cref="Code"/> a tool can branch on, and,
/// where there is one, the <see cref="Suggestion"/> that would resolve it verbatim.</para>
/// </summary>
/// <param name="Severity"><c>error</c>, <c>warning</c> or <c>note</c>.</param>
/// <param name="Code">Dotted and stable, e.g. <c>dependency.implicit</c>.</param>
/// <param name="Path">Where in the document, as a JSON pointer, when it is about one place.</param>
public sealed record DefinitionNote(
    string Severity,
    string Code,
    string Message,
    string? Path = null,
    string? Suggestion = null)
{
    public const string Error = "error";
    public const string Warning = "warning";
    public const string Note = "note";

    public static DefinitionNote Of(string severity, string code, string message,
        string? path = null, string? suggestion = null) => new(severity, code, message, path, suggestion);

    public JsonObject ToJson() => new()
    {
        ["severity"] = Severity,
        ["code"] = Code,
        ["message"] = Message,
        ["path"] = Path,
        ["suggestion"] = Suggestion,
    };

    /// <summary>The one-line form, for a human reading a terminal.</summary>
    public override string ToString() =>
        $"{Severity}: {Message}" + (Suggestion is { Length: > 0 } s ? $" — {s}" : "");
}

/// <summary>
/// One app this app depends on, and how we know.
/// </summary>
/// <param name="Source"><c>declared</c> (named in <c>uses</c>, nothing points there yet),
/// <c>reference</c> (a field points there, the author never said so), or <c>both</c>. The
/// distinction is the whole reason this type exists: intent and fact are different claims, and
/// collapsing them would make the App Contract unable to say which one it is stating.</param>
/// <param name="Fields">The reference fields that observed it, as <c>entity.field</c>.</param>
public sealed record AppDependency(
    string App,
    IReadOnlyList<string> Entities,
    string Source,
    IReadOnlyList<string> Fields,
    string? Why = null)
{
    public const string Declared = "declared";
    public const string Reference = "reference";
    public const string Both = "both";
}

/// <summary>
/// What an app depends on: what its author DECLARED in <c>uses</c>, unioned with what its reference
/// fields actually point at.
///
/// <para><b>The declaration is never edited.</b> An undeclared reference is reported
/// (<see cref="Diagnose"/>) and appears in the computed set as <c>source: reference</c> — the
/// compiler does not write it back into <c>uses</c>. That asymmetry is deliberate: <c>uses</c> is the
/// author's intent and the contract's <c>dependencies</c> is what the compiler knows, and an
/// "improvement" that quietly merged the two would leave nothing able to tell a deliberate
/// dependency from an accidental one.</para>
///
/// <para>The platform directory (<c>targetApp: "platform"</c> — person, department, group) is
/// observed like any other target but is never expected in <c>uses</c>: every app has it, so
/// declaring it would be noise on every definition in existence.</para>
/// </summary>
public static class AppDependencies
{
    public const string PlatformApp = "platform";

    /// <summary>Declared ∪ observed, ordered by app key so two runs produce the same list.</summary>
    public static IReadOnlyList<AppDependency> Of(JsonObject? definition)
    {
        if (definition is null) return [];
        var declared = Declarations(definition);
        var observed = Observations(definition);

        var apps = declared.Keys.Concat(observed.Keys).Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal);
        var result = new List<AppDependency>();
        foreach (var app in apps)
        {
            declared.TryGetValue(app, out var d);
            observed.TryGetValue(app, out var o);
            var source = d is not null && o is not null ? AppDependency.Both
                : d is not null ? AppDependency.Declared
                : AppDependency.Reference;
            var entities = (d?.Entities ?? []).Concat(o?.Entities ?? [])
                .Distinct(StringComparer.Ordinal).OrderBy(e => e, StringComparer.Ordinal).ToList();
            var fields = (o?.Fields ?? []).OrderBy(f => f, StringComparer.Ordinal).ToList();
            result.Add(new AppDependency(app, entities, source, fields, d?.Why));
        }
        return result;
    }

    /// <summary>What is worth saying about those dependencies: a reference into an app the author
    /// never named, and a named app nothing points at.</summary>
    public static IReadOnlyList<DefinitionNote> Diagnose(JsonObject? definition)
    {
        if (definition is null) return [];
        var notes = new List<DefinitionNote>();
        foreach (var dep in Of(definition))
        {
            if (dep.App == PlatformApp) continue;      // every app has the directory; saying so is noise
            if (dep.Source == AppDependency.Reference)
                notes.Add(DefinitionNote.Of(DefinitionNote.Note, "dependency.implicit",
                    $"'{dep.App}' is referenced by {Join(dep.Fields)} but is not declared in `uses`",
                    path: "/uses",
                    suggestion: $"uses: [{{ app: {dep.App}"
                              + (dep.Entities.Count > 0 ? $", entities: [{string.Join(", ", dep.Entities)}]" : "")
                              + " }]"));
            else if (dep.Source == AppDependency.Declared)
                notes.Add(DefinitionNote.Of(DefinitionNote.Warning, "dependency.unused",
                    $"`uses` declares '{dep.App}' but no field references it",
                    path: "/uses"));
        }
        return notes;
    }

    private sealed record Declared(IReadOnlyList<string> Entities, string? Why);

    private static Dictionary<string, Declared> Declarations(JsonObject definition)
    {
        var map = new Dictionary<string, Declared>(StringComparer.Ordinal);
        foreach (var u in definition["uses"] as JsonArray ?? [])
        {
            if (u is not JsonObject use || Str(use, "app") is not { Length: > 0 } app) continue;
            var entities = (use["entities"] as JsonArray ?? [])
                .Select(e => e?.GetValue<string>()).OfType<string>().ToList();
            // A repeated key is a gate error; keeping the first here means the diagnosis of a
            // document that already failed the gate is still stable rather than order-dependent.
            if (!map.ContainsKey(app)) map[app] = new Declared(entities, Str(use, "why"));
        }
        return map;
    }

    private sealed record Observed(HashSet<string> Entities, List<string> Fields);

    private static Dictionary<string, Observed> Observations(JsonObject definition)
    {
        var map = new Dictionary<string, Observed>(StringComparer.Ordinal);
        foreach (var e in definition["entities"] as JsonArray ?? [])
        {
            if (e is not JsonObject entity || Str(entity, "key") is not { Length: > 0 } ekey) continue;
            foreach (var f in entity["fields"] as JsonArray ?? [])
            {
                if (f is not JsonObject field || Str(field, "type") != "reference") continue;
                if (Str(field, "targetApp") is not { Length: > 0 } app) continue;   // a local reference
                if (!map.TryGetValue(app, out var seen))
                    map[app] = seen = new Observed(new HashSet<string>(StringComparer.Ordinal), []);
                if (Str(field, "targetEntity") is { Length: > 0 } target) seen.Entities.Add(target);
                seen.Fields.Add($"{ekey}.{Str(field, "key")}");
            }
        }
        return map;
    }

    private static string Join(IReadOnlyList<string> fields) =>
        fields.Count switch
        {
            0 => "nothing",
            1 => $"'{fields[0]}'",
            _ => $"'{string.Join("', '", fields.Take(3))}'" + (fields.Count > 3 ? $" and {fields.Count - 3} more" : ""),
        };

    private static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

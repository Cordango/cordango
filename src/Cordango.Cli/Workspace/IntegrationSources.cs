// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Integrations;

namespace Cordango.Cli.Workspace;

/// <summary>What the pipeline said about one integration file.</summary>
/// <param name="Hash">The compiled contract's or plan's hash, when it compiled. What a connection pins.</param>
public sealed record IntegrationReport(
    string Path,
    string? Kind,
    string? Name,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Notes,
    string? Hash)
{
    public bool Ok => Errors.Count == 0 && Hash is not null;

    public JsonObject ToJson() => new()
    {
        ["path"] = Path,
        ["kind"] = Kind,
        ["name"] = Name,
        ["ok"] = Ok,
        ["hash"] = Hash,
        ["errors"] = new JsonArray([.. Errors.Select(e => (JsonNode)e!)]),
        ["notes"] = new JsonArray([.. Notes.Select(n => (JsonNode)n!)]),
    };
}

/// <summary>
/// A workspace's integration files: read, compiled, and turned into the roster apps are checked
/// against.
///
/// <para><b>Contracts compile first, then providers against them.</b> A provider is judged by the
/// contracts it claims to implement, so it cannot be compiled before they are known. A contract that
/// fails to compile is left out of the roster — an app naming it then reports "not a known contract"
/// beside the contract's own errors, rather than a cascade of guesses about fields nobody can see.</para>
///
/// <para><b>Never inside an app folder.</b> <see cref="AppFolder"/> reads every <c>*.cordango.yaml</c>
/// under <c>apps/&lt;x&gt;/</c> and refuses a file its app does not list, so an integration file there
/// would break that app with a message about the app. It is refused here instead, with a message
/// about the file.</para>
/// </summary>
public sealed class IntegrationSources
{
    private IntegrationSources(IReadOnlyList<IntegrationReport> reports, KnownIntegrations roster,
        IReadOnlyList<IntegrationContract> contracts, IReadOnlyList<(IntegrationReport Report, JsonObject Document)> documents)
    {
        Reports = reports;
        Roster = roster;
        Contracts = contracts;
        Documents = documents;
    }

    /// <summary>Every file that compiled, with the document it was compiled from, contracts first —
    /// the order <c>publish</c> sends them in, because an instance judges a provider by contracts it
    /// already has.</summary>
    public IReadOnlyList<(IntegrationReport Report, JsonObject Document)> Documents { get; }

    public IReadOnlyList<IntegrationReport> Reports { get; }

    /// <summary>This workspace's contracts that compiled, plus the catalog. A closed set.</summary>
    public KnownIntegrations Roster { get; }

    public IReadOnlyList<IntegrationContract> Contracts { get; }

    public bool AllOk => Reports.All(r => r.Ok);

    public static IntegrationSources Load(WorkspaceFile workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var files = new List<(string Path, JsonObject? Doc, string? Kind, string? Name, List<string> Problems)>();
        var appDirectories = workspace.Apps.Select(workspace.DirectoryOf).ToList();

        foreach (var path in workspace.Integrations)
        {
            var problems = new List<string>();
            var full = workspace.DirectoryOf(path);
            JsonObject? doc = null;
            if (appDirectories.Any(dir => full.StartsWith(dir + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                problems.Add($"{path}: sits inside an app folder, where the app would read it as its own source. "
                           + "Keep integrations under integrations/contracts/ and integrations/providers/");
            else if (!File.Exists(full))
                problems.Add($"{path}: registered in {WorkspaceFile.FileName} but the file does not exist");
            else
            {
                var (parsed, error) = Yaml.Read(File.ReadAllText(full, Yaml.Encoding));
                if (parsed is null) problems.Add($"{path}: {error}");
                else doc = parsed;
            }
            var kind = IntegrationSchemas.KindOf(doc);
            if (doc is not null && kind is null)
                problems.Add($"{path}: needs 'kind: {ContractCompiler.Kind}' or 'kind: {AdapterCompiler.Kind}'");
            var name = doc?["name"] is JsonValue v && v.TryGetValue<string>(out var n) ? n : null;
            files.Add((path, doc, kind, name, problems));
        }

        var reports = new Dictionary<string, IntegrationReport>(StringComparer.Ordinal);
        var contracts = new List<IntegrationContract>();
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in files.Where(f => f.Kind != AdapterCompiler.Kind))
        {
            if (f.Problems.Count > 0 || f.Doc is null)
            { reports[f.Path] = new IntegrationReport(f.Path, f.Kind, f.Name, f.Problems, [], null); continue; }
            var outcome = ContractCompiler.Compile(f.Doc);
            var errors = outcome.Errors.ToList();
            if (outcome.Contract is { } c && !seen.TryAdd(c.Ref.ToString(), f.Path))
                errors.Add($"SEMANTIC: '{c.Ref}' is also defined in {seen[c.Ref.ToString()]}");
            if (errors.Count == 0 && outcome.Contract is { } ok) contracts.Add(ok);
            reports[f.Path] = new IntegrationReport(f.Path, f.Kind, f.Name, errors, [],
                errors.Count == 0 ? outcome.Contract?.Hash : null);
        }

        var roster = KnownIntegrations.Of(contracts);
        foreach (var f in files.Where(f => f.Kind == AdapterCompiler.Kind))
        {
            if (f.Problems.Count > 0 || f.Doc is null)
            { reports[f.Path] = new IntegrationReport(f.Path, f.Kind, f.Name, f.Problems, [], null); continue; }
            var outcome = AdapterCompiler.Compile(f.Doc, roster);
            var errors = outcome.Errors.ToList();
            var key = $"provider:{f.Name}";
            if (outcome.Ok && !seen.TryAdd(key, f.Path))
                errors.Add($"SEMANTIC: provider '{f.Name}' is also defined in {seen[key]}");
            reports[f.Path] = new IntegrationReport(f.Path, f.Kind, f.Name, errors, outcome.Notes,
                errors.Count == 0 ? outcome.Hash : null);
        }

        var documents = files
            .Where(f => f.Doc is not null && reports[f.Path].Ok)
            .OrderBy(f => f.Kind == ContractCompiler.Kind ? 0 : 1)
            .Select(f => (reports[f.Path], f.Doc!))
            .ToList();
        return new IntegrationSources([.. files.Select(f => reports[f.Path])], roster, contracts, documents);
    }
}

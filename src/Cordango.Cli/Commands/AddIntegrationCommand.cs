// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cordango.Cli.Workspace;
using Cordango.Integrations;

namespace Cordango.Cli.Commands;

/// <summary>
/// Add an integration contract or provider to this workspace and register it.
///
/// <para><b>A provider is scaffolded FROM its contract</b>, one entry per entity with every field
/// mapped to a property of the same name, so the first <c>check</c> passes and what is left to do is
/// make the requests and paths match the real API. A blank provider would fail with a dozen "not
/// served" errors that say nothing the scaffold could not have written.</para>
/// </summary>
public static partial class AddIntegrationCommand
{
    public enum Kind { Contract, Provider }

    [GeneratedRegex(@"^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$")]
    private static partial Regex ContractName();

    [GeneratedRegex(@"^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)*$")]
    private static partial Regex ProviderName();

    public static int Run(Args args, Output output, Kind kind)
    {
        var word = kind == Kind.Contract ? "contract" : "provider";
        if (args.First is not { Length: > 0 } name)
            return output.Usage(kind == Kind.Contract
                ? "cordango add contract <name> — e.g. `cordango add contract acme.crm`"
                : "cordango add provider <name> --implements <contract>@<major> — e.g. `cordango add provider acme --implements acme.crm@1`");

        if (kind == Kind.Contract && !ContractName().IsMatch(name))
            return output.Usage($"'{name}' is not a contract name — a contract is '<namespace>.<name>', like acme.crm, so it can never share a name with an app");
        if (kind == Kind.Provider && !ProviderName().IsMatch(name))
            return output.Usage($"'{name}' is not a provider name — lower case, digits, underscores and dots, like acme");

        var workspace = WorkspaceFile.Find(Directory.GetCurrentDirectory(), out var problem);
        if (workspace is null)
            return output.Fail(problem ?? $"no {WorkspaceFile.FileName} in this directory or any parent", [],
                code: ExitCodes.NoWorkspace);

        var path = $"integrations/{word}s/{name}.cordango.yaml";
        if (workspace.Integrations.Contains(path, StringComparer.Ordinal))
            return output.Fail($"{path} is already registered", []);
        var full = workspace.DirectoryOf(path);
        if (File.Exists(full))
            return output.Fail($"{path} already exists but is not registered",
                [$"Add \"{path}\" to `integrations` in {WorkspaceFile.FileName} to use it, or move the file out of the way."]);

        JsonObject document;
        if (kind == Kind.Contract) document = ContractScaffold(name);
        else
        {
            var reference = args.Value("implements");
            var contract = IntegrationSources.Load(workspace).Roster.Find(reference);
            if (contract is null)
                return output.Fail(reference is null
                        ? "a provider implements a contract — give it with --implements <contract>@<major>"
                        : $"'{reference}' is not a contract this workspace knows",
                    ["`cordango inspect` lists the contracts an app or a provider may name."]);
            document = ProviderScaffold(name, contract);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        AtomicFile.Write(full, Yaml.Write(document));
        (workspace with { Integrations = [.. workspace.Integrations, path] }).Save();

        return output.Ok(
            new JsonObject { ["kind"] = word, ["name"] = name, ["path"] = path },
            w =>
            {
                w.WriteLine($"Added {word} {name} in {path} and registered it in {WorkspaceFile.FileName}.");
                w.WriteLine(kind == Kind.Contract
                    ? "  Describe its entities, then: cordango check"
                    : "  Point its paths and response paths at the real API, then: cordango check");
            });
    }

    private static string Label(string key) =>
        key.Length == 0 ? key : char.ToUpperInvariant(key[0]) + key[1..].Replace('_', ' ');

    private static JsonObject ContractScaffold(string name) => new()
    {
        ["kind"] = ContractCompiler.Kind,
        ["name"] = name,
        ["version"] = 1,
        ["label"] = Label(name[(name.LastIndexOf('.') + 1)..]),
        ["entities"] = new JsonObject
        {
            ["record"] = new JsonObject
            {
                ["displayField"] = "name",
                ["fields"] = new JsonObject
                {
                    ["id"] = new JsonObject { ["type"] = "text", ["readonly"] = true },
                    ["name"] = new JsonObject { ["type"] = "text", ["required"] = true },
                },
                ["capabilities"] = new JsonArray("list", "get"),
            },
        },
    };

    private static JsonObject ProviderScaffold(string name, IntegrationContract contract)
    {
        var entities = new JsonObject();
        foreach (var entity in contract.Entities)
        {
            var mapping = new JsonObject();
            foreach (var field in entity.Fields) mapping[field.Key] = $"$.{field.Key}";
            var operations = new JsonObject();
            if (entity.Capabilities.Contains("list"))
                operations["list"] = new JsonObject
                {
                    ["path"] = $"/{entity.Key}s",
                    ["paging"] = new JsonObject { ["style"] = "none" },
                    ["response"] = new JsonObject { ["items"] = "$.items" },
                };
            if (entity.Capabilities.Contains("get"))
                operations["get"] = new JsonObject { ["path"] = $"/{entity.Key}s/{{{{id}}}}" };
            entities[entity.Key] = new JsonObject
            {
                ["operations"] = operations,
                ["mapping"] = new JsonObject { ["fromProvider"] = mapping },
            };
        }

        return new JsonObject
        {
            ["kind"] = AdapterCompiler.Kind,
            ["name"] = name,
            ["version"] = 1,
            ["label"] = Label(name),
            ["implements"] = new JsonArray(contract.Ref.ToString()),
            ["http"] = new JsonObject { ["baseUrl"] = "https://api.example.com" },
            ["auth"] = new JsonObject { ["type"] = "bearer", ["scope"] = "workspace", ["label"] = "API token" },
            ["entities"] = entities,
        };
    }
}

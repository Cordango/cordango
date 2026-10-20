// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Cli.Commands;
using Cordango.Cli.Workspace;

namespace Cordango.Cli.Tests;

public class IntegrationWorkspaceTests
{
    private const string Contract = """
        kind: IntegrationContract
        name: acme.crm
        version: 1
        entities:
          company:
            displayField: name
            fields:
              id: { type: text, readonly: true }
              name: { type: text, required: true }
              industry: { type: select, options: [software, retail] }
            capabilities: [list, get]
            query:
              filter: { industry: [eq] }
              aggregate: { count: true }
        """;

    private const string Provider = """
        kind: IntegrationProvider
        name: acme
        version: 1
        implements: [acme.crm@1]
        http: { baseUrl: "https://api.acme.example/v3" }
        auth: { type: bearer, scope: workspace }
        entities:
          company:
            operations:
              list:
                path: /companies
                paging: { style: offset, param: offset, sizeParam: limit, size: 50 }
                response: { items: $.data, total: $.meta.total }
                filters: { fields: { industry: { name: industry } }, query: "{{filter.name}}" }
              get: { path: "/companies/{{id}}" }
            aggregate: { count: { via: list } }
            mapping:
              fromProvider:
                id: $.id
                name: $.name
                industry: $.industry
        """;

    private static Sandbox Workspace(string contract = Contract, string provider = Provider)
    {
        var sandbox = new Sandbox();
        sandbox.Run("new", "sales");
        sandbox.Write("integrations/contracts/acme.crm.cordango.yaml", contract);
        sandbox.Write("integrations/providers/acme.cordango.yaml", provider);
        Register(sandbox, "integrations/contracts/acme.crm.cordango.yaml", "integrations/providers/acme.cordango.yaml");
        return sandbox;
    }

    private static void Register(Sandbox sandbox, params string[] paths)
    {
        var file = sandbox.Path_(WorkspaceFile.FileName);
        var text = File.ReadAllText(file).Replace("formatVersion: 1", "formatVersion: 2", StringComparison.Ordinal);
        File.WriteAllText(file, text.TrimEnd() + "\nintegrations:\n" + string.Concat(paths.Select(p => $"  - {p}\n")));
    }

    private static void Uses(Sandbox sandbox, string integration) =>
        File.AppendAllText(sandbox.Path_("apps", "sales", "app.cordango.yaml"),
            $"\nuses:\n  - integration: {integration}\n    as: crm\n");

    private static JsonObject App(JsonObject payload) => payload["apps"]!.AsArray().OfType<JsonObject>().First();

    [Fact]
    public void A_workspace_without_integrations_stays_at_format_one()
    {
        using var sandbox = new Sandbox();
        sandbox.Run("new", "sales");

        Assert.Contains("formatVersion: 1", File.ReadAllText(sandbox.Path_(WorkspaceFile.FileName)), StringComparison.Ordinal);
    }

    [Fact]
    public void Registered_contracts_and_providers_are_checked()
    {
        using var sandbox = Workspace();

        var (exit, payload) = sandbox.RunJson("check");

        Assert.Equal(ExitCodes.Ok, exit);
        var integrations = payload["integrations"]!.AsArray().OfType<JsonObject>().ToList();
        Assert.Equal(2, integrations.Count);
        Assert.All(integrations, i => Assert.True((bool)i["ok"]!, i.ToJsonString()));
    }

    [Fact]
    public void An_app_may_use_a_workspace_contract()
    {
        using var sandbox = Workspace();
        Uses(sandbox, "acme.crm@1");

        var (exit, payload) = sandbox.RunJson("check");

        Assert.Equal(ExitCodes.Ok, exit);
        Assert.True((bool)App(payload)["coherent"]!, sandbox.Out);
    }

    [Fact]
    public void An_app_may_use_a_catalog_contract_with_nothing_registered()
    {
        using var sandbox = new Sandbox();
        sandbox.Run("new", "sales");
        Uses(sandbox, "hubspot.crm@1");

        Assert.Equal(ExitCodes.Ok, sandbox.Run("check"));
    }

    [Fact]
    public void A_contract_that_is_nowhere_is_refused()
    {
        using var sandbox = Workspace();
        Uses(sandbox, "acme.erp@1");

        var (exit, payload) = sandbox.RunJson("check");

        Assert.NotEqual(ExitCodes.Ok, exit);
        Assert.Contains(App(payload)["errors"]!.AsArray().Select(e => (string)e!),
            e => e.Contains("names integration 'acme.erp@1', which is not a known contract"));
    }

    [Fact]
    public void A_provider_that_breaks_its_contract_fails_the_check()
    {
        using var sandbox = Workspace(provider: Provider.Replace("name: $.name", "", StringComparison.Ordinal));

        Assert.NotEqual(ExitCodes.Ok, sandbox.Run("check"));
        Assert.Contains("'name' is required by the contract and is not mapped", sandbox.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_secret_in_a_template_fails_the_check()
    {
        using var sandbox = Workspace(provider: Provider.Replace("path: /companies\n", "path: \"/companies/{{token}}\"\n", StringComparison.Ordinal));

        Assert.NotEqual(ExitCodes.Ok, sandbox.Run("check"));
        Assert.Contains("A credential is never a template variable", sandbox.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_integration_file_inside_an_app_folder_is_refused()
    {
        using var sandbox = new Sandbox();
        sandbox.Run("new", "sales");
        sandbox.Write("apps/sales/acme.crm.cordango.yaml", Contract);
        Register(sandbox, "apps/sales/acme.crm.cordango.yaml");

        Assert.NotEqual(ExitCodes.Ok, sandbox.Run("check"));
        Assert.Contains("sits inside an app folder", sandbox.Error + sandbox.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void Integrations_under_format_one_are_refused_rather_than_half_read()
    {
        using var sandbox = Workspace();
        var file = sandbox.Path_(WorkspaceFile.FileName);
        File.WriteAllText(file, File.ReadAllText(file).Replace("formatVersion: 2", "formatVersion: 1", StringComparison.Ordinal));

        Assert.NotEqual(ExitCodes.Ok, sandbox.Run("check"));
        Assert.Contains("integrations need formatVersion 2", sandbox.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_newer_format_is_refused()
    {
        using var sandbox = Workspace();
        var file = sandbox.Path_(WorkspaceFile.FileName);
        File.WriteAllText(file, File.ReadAllText(file).Replace("formatVersion: 2", "formatVersion: 3", StringComparison.Ordinal));

        Assert.NotEqual(ExitCodes.Ok, sandbox.Run("check"));
        Assert.Contains("formatVersion 3", sandbox.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Saving_the_workspace_keeps_its_integrations()
    {
        using var sandbox = Workspace();

        sandbox.Run("add", "app", "reports");

        var text = File.ReadAllText(sandbox.Path_(WorkspaceFile.FileName));
        Assert.Contains("formatVersion: 2", text, StringComparison.Ordinal);
        Assert.Contains("integrations/providers/acme.cordango.yaml", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_scaffolded_contract_and_provider_check_clean()
    {
        using var sandbox = new Sandbox();
        sandbox.Run("new", "sales");

        Assert.Equal(ExitCodes.Ok, sandbox.Run("add", "contract", "acme.crm"));
        Assert.Equal(ExitCodes.Ok, sandbox.Run("add", "provider", "acme", "--implements", "acme.crm@1"));

        var (exit, payload) = sandbox.RunJson("check");
        Assert.Equal(ExitCodes.Ok, exit);
        Assert.Equal(2, payload["integrations"]!.AsArray().Count);
        Assert.True(File.Exists(sandbox.Path_("integrations", "providers", "acme.cordango.yaml")));
        Assert.Contains("formatVersion: 2", File.ReadAllText(sandbox.Path_(WorkspaceFile.FileName)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_provider_needs_a_contract_to_implement()
    {
        using var sandbox = new Sandbox();
        sandbox.Run("new", "sales");

        Assert.NotEqual(ExitCodes.Ok, sandbox.Run("add", "provider", "acme"));
        Assert.Contains("--implements", sandbox.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_contract_name_without_a_namespace_is_refused()
    {
        using var sandbox = new Sandbox();
        sandbox.Run("new", "sales");

        Assert.NotEqual(ExitCodes.Ok, sandbox.Run("add", "contract", "crm"));
        Assert.Contains("'<namespace>.<name>'", sandbox.Error + sandbox.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void Inspect_lists_the_contracts_an_app_may_read()
    {
        using var sandbox = Workspace();

        var (_, payload) = sandbox.RunJson("inspect");

        var refs = payload["integrations"]!.AsArray().OfType<JsonObject>().Select(i => (string)i["ref"]!).ToList();
        Assert.Contains("acme.crm@1", refs);
        Assert.Contains("hubspot.crm@1", refs);
    }

    private static void Connect(Sandbox sandbox, FakeInstance instance)
    {
        var workspace = WorkspaceFile.Find(sandbox.Root, out _)!;
        var credentials = Cordango.Cli.Remote.Credentials.Load();
        credentials.Save(new Cordango.Cli.Remote.InstanceLogin(instance.Origin, "cord_pat.a.b.c", "default",
            "t@example.com", DateTimeOffset.UtcNow));
        credentials.Bind(workspace.WorkspaceId, instance.Origin);
        credentials.Flush();
    }

    [Fact]
    public void Publishing_sends_contracts_then_providers_then_apps()
    {
        using var sandbox = Workspace();
        using var instance = new FakeInstance();
        Connect(sandbox, instance);

        Assert.Equal(ExitCodes.Ok, sandbox.Run("publish"));

        var posts = instance.Received.Where(r => r.Method == "POST").ToList();
        Assert.Equal(3, posts.Count);
        Assert.Equal("IntegrationContract", (string?)posts[0].Body?["document"]?["kind"]);
        Assert.Equal("IntegrationProvider", (string?)posts[1].Body?["document"]?["kind"]);
        Assert.Equal("/api/apps/publish", posts[2].Path);
    }

    [Fact]
    public void A_refused_definition_stops_the_run_before_any_app_is_sent()
    {
        using var sandbox = Workspace();
        using var instance = new FakeInstance { IntegrationStatus = 409 };
        Connect(sandbox, instance);

        Assert.NotEqual(ExitCodes.Ok, sandbox.Run("publish"));

        Assert.DoesNotContain(instance.Received, r => r.Path == "/api/apps/publish");
        Assert.Single(instance.Received, r => r.Method == "POST");
    }

    [Fact]
    public void The_platform_refuses_it_until_it_can_connect_one()
    {
        using var sandbox = Workspace();
        Uses(sandbox, "acme.crm@1");

        var (exit, payload) = sandbox.RunJson("check", "--target", "platform");

        Assert.NotEqual(ExitCodes.Ok, exit);
        Assert.Contains(payload["unsupported"]!.AsArray().OfType<JsonObject>(), u => (string?)u["code"] == "CORD2311");
    }

    [Fact]
    public void A_standalone_target_refuses_an_app_that_reads_an_integration()
    {
        using var sandbox = Workspace();
        Uses(sandbox, "acme.crm@1");

        var (exit, payload) = sandbox.RunJson("check", "--target", "standalone");

        Assert.NotEqual(ExitCodes.Ok, exit);
        Assert.Contains(payload["unsupported"]!.AsArray().OfType<JsonObject>(), u => (string?)u["code"] == "CORD2311");
    }
}

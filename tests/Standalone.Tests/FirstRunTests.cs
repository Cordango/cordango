// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Compile;
using Cordango.SourceGen;
using Cordango.SourceGen.DotNetVue;

namespace Cordango.Standalone.Tests;

public class FirstRunTests
{
    [Fact]
    public void The_scaffold_ships_the_first_run_screen()
    {
        var files = Scaffold.Files(new ScaffoldOptions("Expenses", "expenses", "Expenses"))
            .ToDictionary(f => f.RelativePath, f => f.Content, StringComparer.Ordinal);

        Assert.Contains("web/src/views/SetupView.vue", files.Keys);

        var controller = files["api/Identity/AccountController.cs"];
        Assert.Contains("[HttpPost(\"setup\")]", controller, StringComparison.Ordinal);
        Assert.Contains("[AllowAnonymous]", controller, StringComparison.Ordinal);

        Assert.Contains("AdministratorExistsAsync", controller, StringComparison.Ordinal);
        Assert.Contains("setup.completed", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void Startup_invents_no_password()
    {
        var identity = Scaffold.Files(new ScaffoldOptions("Expenses", "expenses", "Expenses"))
            .Single(f => f.RelativePath == "api/Identity/AppIdentity.cs")
            .Content;

        Assert.Contains("Admin:Password", identity, StringComparison.Ordinal);
        Assert.DoesNotContain("GeneratePassword", identity, StringComparison.Ordinal);
        Assert.DoesNotContain("RandomNumberGenerator", identity, StringComparison.Ordinal);
    }

    [Fact]
    public void The_generated_router_sends_the_first_visitor_to_setup()
    {
        var router = Generate("expenses").Files
            .Single(f => f.RelativePath == "web/src/router.js")
            .Content;

        Assert.Contains("import SetupView from './views/SetupView.vue'", router, StringComparison.Ordinal);
        Assert.Contains("name: 'setup'", router, StringComparison.Ordinal);
        Assert.Contains("if (session.setupRequired)", router, StringComparison.Ordinal);

        Assert.Contains("session.authenticated ? { name: 'home' } : { name: 'login' }", router, StringComparison.Ordinal);
    }

    private static GenerateResult Generate(string key)
    {
        var path = Path.Combine(TestPaths.RepoRoot(), "tests", "corpus", "reference", key + ".appdef.json");
        var definition = JsonNode.Parse(File.ReadAllText(path))!.AsObject();

        var outcome = CandidateValidator.Run(definition, key, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.True(outcome.Manifest is not null, $"{key} did not compile.");

        var artifact = new CompiledAppArtifact(
            outcome.Definition!.AsObject(),
            outcome.Manifest!,
            outcome.Hash ?? "unhashed",
            new CompilerInfo("test", "1"));

        return new DotNetVueGenerator().Generate(new GenerateRequest(artifact, new JsonObject
        {
            ["allowIncomplete"] = true,
            ["seed"] = 42,
        }));
    }
}

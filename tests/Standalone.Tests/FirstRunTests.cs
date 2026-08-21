// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Compile;
using Cordango.SourceGen;
using Cordango.SourceGen.DotNetVue;

namespace Cordango.Standalone.Tests;

/// <summary>
/// Nobody is handed a password they did not choose, and there is always a way in.
///
/// <para>Those two pull against each other, and the two obvious resolutions both fail. A DEFAULT
/// password means every application this toolchain produces ships with the same credentials. A
/// GENERATED one closes that hole and can only be delivered by printing it to a log — which is no
/// answer at all for somebody who started the application and opened a browser, and it was the first
/// thing tried here.</para>
///
/// <para>So the first person to reach a database with no administrator is asked to create one, and
/// the endpoint behind that form stops answering the moment an account exists. These tests pin the
/// pieces that have to be present for that to be true, because every one of them is invisible until
/// somebody runs a brand-new application and finds a sign-in form they cannot pass.</para>
/// </summary>
public class FirstRunTests
{
    [Fact]
    public void The_scaffold_ships_the_first_run_screen()
    {
        var files = Scaffold.Files(new ScaffoldOptions("Expenses", "expenses", "Expenses"))
            .ToDictionary(f => f.RelativePath, f => f.Content, StringComparer.Ordinal);

        Assert.Contains("web/src/views/SetupView.vue", files.Keys);

        // Anonymous, because a database with no administrator has nobody who could authenticate.
        var controller = files["api/Identity/AccountController.cs"];
        Assert.Contains("[HttpPost(\"setup\")]", controller, StringComparison.Ordinal);
        Assert.Contains("[AllowAnonymous]", controller, StringComparison.Ordinal);

        // And it closes itself. Without this check the endpoint is a way for anybody to mint an
        // administrator on a running application, which is the failure this whole design exists to
        // avoid.
        Assert.Contains("AdministratorExistsAsync", controller, StringComparison.Ordinal);
        Assert.Contains("setup.completed", controller, StringComparison.Ordinal);
    }

    /// <summary>
    /// Startup creates an account only when somebody configured its password.
    ///
    /// <para>A negative assertion, deliberately: the failure it guards against is somebody
    /// reintroducing a convenient default or a generated-and-logged password, and both would be
    /// invisible in every other test in this repository — the application would build, start, and
    /// pass its whole suite while shipping credentials nobody chose.</para>
    /// </summary>
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

    /// <summary>
    /// The generated router sends the first visitor to the setup screen and nowhere else.
    ///
    /// <para>Asserted on the EMITTED router rather than the scaffold's own, because that file is
    /// regenerated per application: the scaffold's copy could be perfect while every generated
    /// application dropped the route.</para>
    /// </summary>
    [Fact]
    public void The_generated_router_sends_the_first_visitor_to_setup()
    {
        var router = Generate("expenses").Files
            .Single(f => f.RelativePath == "web/src/router.js")
            .Content;

        Assert.Contains("import SetupView from './views/SetupView.vue'", router, StringComparison.Ordinal);
        Assert.Contains("name: 'setup'", router, StringComparison.Ordinal);
        Assert.Contains("if (session.setupRequired)", router, StringComparison.Ordinal);

        // Once an administrator exists the route is not a page any more, and a signed-in person who
        // types the address goes home rather than to a login form they have already passed.
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

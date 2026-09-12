// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.SourceGen.NodeVue;

namespace Cordango.NodeVue.Tests;

/// <summary>Everything an application has before a single entity is generated into it.</summary>
public class ScaffoldTests
{
    private static readonly ScaffoldOptions Expenses = new("Expense Claims", "expense-claims");

    /// <summary>
    /// Two generated applications do not collide on one machine.
    ///
    /// <para>Compose names the project after the directory it runs in, and every application this
    /// generator produces is built into one somebody called <c>generated</c>. So two of them were
    /// both the project <c>generated</c>: same containers, same network, and the same
    /// <c>generated_db</c> volume — the second `docker compose up` attached to the first one's
    /// database rather than starting anything. The file names the project itself.</para>
    /// </summary>
    [Fact]
    public void Compose_names_the_project_and_its_containers_after_the_application()
    {
        var compose = Scaffold.Files(new ScaffoldOptions("Budget Planner", "budget_planner"))
            .Single(f => f.RelativePath == "docker-compose.yml").Content;

        // Underscores are legal in a Compose project name and wrong in a hostname, and the key is
        // the only place a definition can put one.
        Assert.Contains("\nname: budget-planner\n", compose, StringComparison.Ordinal);
        Assert.Contains("container_name: budget-planner-db", compose, StringComparison.Ordinal);
        Assert.Contains("container_name: budget-planner-app", compose, StringComparison.Ordinal);

        Assert.DoesNotContain("name: budget_planner", compose, StringComparison.Ordinal);
    }

    [Fact]
    public void It_has_a_host_a_package_and_a_front_end()
    {
        var paths = Scaffold.Files(Expenses).Select(f => f.RelativePath).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("api/src/server.ts", paths);
        Assert.Contains("api/package.json", paths);
        Assert.Contains("api/tsconfig.json", paths);
        Assert.Contains("api/src/resources/messages.de.json", paths);
        Assert.Contains("web/src/main.js", paths);
        Assert.Contains("Dockerfile", paths);
        Assert.Contains("docker-compose.yml", paths);
        Assert.Contains("README.md", paths);
        Assert.Contains(".env.example", paths);
    }

    [Fact]
    public void Nothing_is_emitted_with_the_template_suffix()
    {
        Assert.DoesNotContain(Scaffold.Files(Expenses),
            f => f.RelativePath.EndsWith(".template", StringComparison.Ordinal));
    }

    /// <summary>The runtime is pinned to the exact version of the generator that wrote the
    /// package.json. The two publish from one tag and cannot be installed apart, so a range
    /// operator here would let `npm install` pick up a runtime this generator has never emitted
    /// against, in an application somebody generated months ago, on a machine where the only thing
    /// that changed was the day.</summary>
    [Fact]
    public void The_runtime_is_pinned_exactly()
    {
        var package = Scaffold.Files(Expenses).Single(f => f.RelativePath == "api/package.json").Content;

        Assert.Contains($"\"@cordango/standalone\": \"{Scaffold.RuntimeVersion}\"", package,
            StringComparison.Ordinal);
        Assert.DoesNotContain("\"@cordango/standalone\": \"^", package, StringComparison.Ordinal);
        Assert.DoesNotContain("\"@cordango/standalone\": \"~", package, StringComparison.Ordinal);
    }

    /// <summary>The scaffold version answers "which scaffold produced this application" years
    /// later, so it changes when the scaffold does and not otherwise.</summary>
    [Fact]
    public void The_scaffold_version_is_derived_from_its_own_contents()
    {
        Assert.StartsWith("1.0.0+", Scaffold.Version, StringComparison.Ordinal);
        Assert.Equal(Scaffold.Version, Scaffold.Version);
    }

    /// <summary>
    /// The generated host refuses to start without a signing secret.
    ///
    /// <para>There is no default and there cannot be one: an application shipping with a known
    /// signing key is an application anybody can forge a session for. Asserted on the emitted
    /// source, because this is the kind of safety rail that gets "temporarily" defaulted.</para>
    /// </summary>
    [Fact]
    public void The_host_will_not_start_without_a_signing_secret()
    {
        var server = Scaffold.Files(Expenses).Single(f => f.RelativePath == "api/src/server.ts").Content;

        Assert.Contains("APP_SECRET", server, StringComparison.Ordinal);
        Assert.Contains("process.exit(1)", server, StringComparison.Ordinal);
    }

    /// <summary>Every token the scaffold declares is one the substitution pass actually replaces.
    /// A token in the list and not in a file is harmless; a token in a FILE and not in the list
    /// ships as literal text, so the list is what a test can hold onto.</summary>
    [Fact]
    public void No_declared_token_survives_substitution()
    {
        foreach (var file in Scaffold.Files(Expenses))
            foreach (var token in Scaffold.Tokens)
                Assert.False(file.Content.Contains(token, StringComparison.Ordinal),
                    $"{file.RelativePath} still contains {token}.");
    }
}

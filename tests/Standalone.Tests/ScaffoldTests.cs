// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.SourceGen.DotNetVue;

namespace Cordango.Standalone.Tests;

public class ScaffoldTests
{
    private static readonly ScaffoldOptions Expenses = new("Expense Claims", "expense-claims", "ExpenseClaims");

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
        var compose = Scaffold.Files(new ScaffoldOptions("Budget Planner", "budget_planner", "BudgetPlanner"))
            .Single(f => f.RelativePath == "docker-compose.yml").Content;

        // Underscores are legal in a Compose project name and wrong in a hostname, and the key is
        // the only place a definition can put one.
        Assert.Contains("\nname: budget-planner\n", compose, StringComparison.Ordinal);
        Assert.Contains("container_name: budget-planner-db", compose, StringComparison.Ordinal);
        Assert.Contains("container_name: budget-planner-app", compose, StringComparison.Ordinal);

        Assert.DoesNotContain("name: budget_planner", compose, StringComparison.Ordinal);
    }

    [Fact]
    public void It_has_a_host_a_front_end_and_the_runtime()
    {
        var paths = Scaffold.Files(Expenses).Select(f => f.RelativePath).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("api/Program.cs", paths);
        Assert.Contains("api/ExpenseClaims.Api.csproj", paths);
        Assert.Contains("api/Identity/AppIdentity.cs", paths);
        Assert.Contains("api/Resources/messages.de.json", paths);
        Assert.Contains("web/src/main.js", paths);
        Assert.Contains("Dockerfile", paths);
        Assert.Contains("docker-compose.yml", paths);
        Assert.Contains("README.md", paths);

        Assert.Contains("runtime/Security/PermissionResolver.cs", paths);
        Assert.Contains("runtime/Http/RecordsController.cs", paths);
        Assert.Contains("runtime/Directory/Entities.cs", paths);
    }

    [Fact]
    public void Nothing_is_emitted_with_the_template_suffix()
    {
        Assert.DoesNotContain(Scaffold.Files(Expenses),
            f => f.RelativePath.EndsWith(".template", StringComparison.Ordinal));
    }

    [Fact]
    public void Placeholders_are_substituted_in_paths_and_in_content()
    {
        var files = Scaffold.Files(Expenses).ToDictionary(f => f.RelativePath, f => f.Content, StringComparer.Ordinal);

        Assert.Contains("<RootNamespace>ExpenseClaims</RootNamespace>", files["api/ExpenseClaims.Api.csproj"]);
        Assert.Contains("namespace ExpenseClaims.Data;", files["api/Data/AppDbContext.cs"]);
        Assert.Contains("# Expense Claims", files["README.md"]);
        Assert.Contains("POSTGRES_DB: expense-claims", files["docker-compose.yml"]);
    }

    [Fact]
    public void No_placeholder_survives_into_the_output()
    {
        Assert.True(Scaffold.Tokens.Count >= 4);

        foreach (var file in Scaffold.Files(Expenses))
            foreach (var token in Scaffold.Tokens)
            {
                Assert.False(file.RelativePath.Contains(token, StringComparison.Ordinal),
                    $"{file.RelativePath}: {token} survived in a PATH.");
                Assert.False(file.Content.Contains(token, StringComparison.Ordinal),
                    $"{file.RelativePath}: {token} survived in the content.");
            }
    }

    [Fact]
    public void The_same_options_produce_the_same_files_in_the_same_order()
    {
        var first = Scaffold.Files(Expenses);
        var second = Scaffold.Files(Expenses);

        Assert.Equal(first.Select(f => f.RelativePath), second.Select(f => f.RelativePath));
        Assert.Equal(first.Select(f => f.Content), second.Select(f => f.Content));
        Assert.Equal(first.Select(f => f.RelativePath).Order(StringComparer.Ordinal), first.Select(f => f.RelativePath));
    }

    [Fact]
    public void Line_endings_are_normalised()
    {
        foreach (var file in Scaffold.Files(Expenses))
            Assert.False(file.Content.Contains('\r'),
                $"{file.RelativePath} carries a carriage return. The scaffold reader normalises on the way in; something bypassed it.");
    }

    [Fact]
    public void The_version_is_a_fingerprint_of_the_contents()
    {
        Assert.Matches(@"^1\.0\.0\+[0-9a-f]{12}$", Scaffold.Version);
        Assert.Equal(Scaffold.Version, Scaffold.Version);
    }

    [Fact]
    public void The_runtime_is_stamped_and_the_users_code_is_not()
    {
        const string marker = "SPDX-License-Identifier: Apache-2.0";

        foreach (var file in Scaffold.Files(Expenses))
        {
            var head = string.Join('\n', file.Content.Split('\n').Take(6));
            var isRuntime = file.RelativePath.StartsWith("runtime/", StringComparison.Ordinal)
                && file.RelativePath.EndsWith(".cs", StringComparison.Ordinal);

            if (isRuntime)
                Assert.True(head.Contains(marker, StringComparison.Ordinal),
                    $"{file.RelativePath} is Cordango runtime source and has lost its licence header.");
            else
                Assert.False(head.Contains(marker, StringComparison.Ordinal),
                    $"{file.RelativePath} is the user's file and must not carry our copyright.");
        }
    }

    [Fact]
    public void The_runtime_is_beside_the_application_and_not_inside_it()
    {
        var files = Scaffold.Files(Expenses);

        Assert.DoesNotContain(files, f => f.RelativePath.StartsWith("api/Cordango/", StringComparison.Ordinal));
        Assert.Contains(files, f => f.RelativePath == "runtime/Cordango.Standalone.csproj");

        var project = files.Single(f => f.RelativePath == "api/ExpenseClaims.Api.csproj").Content;
        Assert.Contains(@"<ProjectReference Include=""..\runtime\Cordango.Standalone.csproj"" />",
            project, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("AddOpenApi(", "Microsoft.AspNetCore.OpenApi")]
    [InlineData("MapOpenApi(", "Microsoft.AspNetCore.OpenApi")]
    [InlineData("MapScalarApiReference(", "Scalar.AspNetCore")]
    public void A_package_the_application_calls_is_referenced_by_the_application(string call, string package)
    {
        var files = Scaffold.Files(Expenses);

        var callers = files
            .Where(f => f.RelativePath.StartsWith("api/", StringComparison.Ordinal)
                && f.RelativePath.EndsWith(".cs", StringComparison.Ordinal)
                && f.Content.Contains(call, StringComparison.Ordinal))
            .Select(f => f.RelativePath)
            .ToList();

        Assert.True(callers.Count > 0, $"No file under api/ calls {call} any more, so this row is stale.");

        var project = files.Single(f => f.RelativePath == "api/ExpenseClaims.Api.csproj").Content;

        Assert.True(
            project.Contains($@"<PackageReference Include=""{package}""", StringComparison.Ordinal),
            $"{string.Join(", ", callers)} calls {call} but api/ExpenseClaims.Api.csproj does not reference "
            + $"{package} directly. Reaching it through the runtime's ProjectReference is not enough: restore "
            + "prunes transitive references to packages the framework is deemed to provide, and the build then "
            + "fails on some machines and not others.");
    }

    [Fact]
    public void Referencing_the_runtime_as_a_package_leaves_nothing_to_check_in()
    {
        var files = Scaffold.Files(Expenses with { RuntimeAsPackage = true });

        Assert.DoesNotContain(files, f => f.RelativePath.StartsWith("runtime/", StringComparison.Ordinal));

        var project = files.Single(f => f.RelativePath == "api/ExpenseClaims.Api.csproj").Content;
        Assert.Contains(
            $@"<PackageReference Include=""Cordango.Standalone"" Version=""{Scaffold.RuntimeVersion}"" />",
            project, StringComparison.Ordinal);
    }

    [Fact]
    public void The_partial_build_section_appears_only_when_it_is_supplied()
    {
        var whole = Scaffold.Files(Expenses).Single(f => f.RelativePath == "README.md").Content;
        Assert.DoesNotContain("Partial build", whole, StringComparison.Ordinal);

        var scarred = Scaffold.Files(Expenses with
        {
            PartialBuildSection = "\n## Partial build\n\nSome screens could not be generated.\n",
        }).Single(f => f.RelativePath == "README.md").Content;

        Assert.Contains("## Partial build", scarred, StringComparison.Ordinal);
    }

    [Fact]
    public void The_scaffold_is_not_quietly_empty()
    {
        var files = Scaffold.Files(Expenses);
        Assert.True(files.Count >= 40,
            $"Only {files.Count} scaffold files were embedded. The glob in Cordango.SourceGen.DotNetVue.csproj "
            + "has probably stopped matching.");

        Assert.True(files.Count(f => f.RelativePath.StartsWith("runtime/", StringComparison.Ordinal)) >= 12,
            "The runtime source did not come along.");
    }

    [Fact]
    public void Every_runtime_source_file_is_embedded()
    {
        var onDisk = System.IO.Directory
            .EnumerateFiles(RuntimeSourceDirectory, "*.cs", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(RuntimeSourceDirectory, p).Replace('\\', '/'))
            .Where(p => !p.StartsWith("Templates/", StringComparison.Ordinal)
                        && !p.StartsWith("bin/", StringComparison.Ordinal)
                        && !p.StartsWith("obj/", StringComparison.Ordinal))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        var embedded = Scaffold.Files(Expenses)
            .Where(f => f.RelativePath.StartsWith("runtime/", StringComparison.Ordinal)
                && f.RelativePath.EndsWith(".cs", StringComparison.Ordinal))
            .Select(f => f.RelativePath["runtime/".Length..])
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(onDisk, embedded);
    }

    private static string RuntimeSourceDirectory =>
        Path.Combine(TestPaths.RepoRoot(), "src", "Cordango.Standalone");
}

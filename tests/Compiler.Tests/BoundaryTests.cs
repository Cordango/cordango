// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Reflection;
using Cordango.Cord;
using Cordango.TestCorpus;

namespace Cordango.Compiler.Tests;

public class BoundaryTests
{
    private static readonly string[] Allowed =
    [
        "JsonSchema.Net",
        "Json.More",
        "JsonPointer.Net",
        "netstandard",
        "System.Runtime",
    ];

    private static readonly string[] Forbidden =
    [
        "AppBuilder.Generator",
        "AppBuilder.Platform",
        "AppBuilder.Runtime",
        "AppBuilder.ControlPlane",
        "AppBuilder.Localization",
        "AppBuilder.Api",
        "AppBuilder.Worker",
        "Microsoft.EntityFrameworkCore",
        "Npgsql",
    ];

    private static Assembly Compiler => typeof(CordApp).Assembly;

    [Fact]
    public void The_compiler_references_nothing_outside_the_allowed_set()
    {
        var unexpected = Compiler.GetReferencedAssemblies()
            .Select(a => a.Name ?? "")
            .Where(n => !Allowed.Any(ok => n == ok || n.StartsWith(ok + ".", StringComparison.Ordinal)))
            .Where(n => !n.StartsWith("System.", StringComparison.Ordinal) && n != "System")
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(unexpected.Count == 0,
            "Cordango.Compiler must stay independently extractable (rule 0). Unexpected reference(s): "
            + string.Join(", ", unexpected)
            + ". If one of these is genuinely needed, the answer is almost always an adapter in the "
            + "host, not a reference here.");
    }

    [Fact]
    public void The_compiler_references_none_of_the_forbidden_concerns()
    {
        var names = Compiler.GetReferencedAssemblies().Select(a => a.Name ?? "").ToHashSet(StringComparer.Ordinal);
        foreach (var banned in Forbidden)
            Assert.False(names.Contains(banned),
                $"Cordango.Compiler references {banned}. The dependency direction is host -> compiler, "
                + "never the reverse, and persistence/hosting never reach the compiler at all.");
    }

    [Fact]
    public void The_csproj_declares_no_project_references_and_exactly_one_package()
    {
        var csproj = File.ReadAllText(Path.Combine(
            Corpus.RepoRoot(), "src", "Cordango.Compiler", "Cordango.Compiler.csproj"));

        var references = csproj.Split("<ProjectReference").Length - 1;
        Assert.True(references == 0, $"expected no ProjectReference, found {references}");

        var packages = csproj.Split("<PackageReference").Length - 1;
        Assert.True(packages == 1, $"expected exactly one PackageReference, found {packages}");
        Assert.Contains("JsonSchema.Net", csproj);
    }

    [Theory]
    [InlineData("Anthropic")]
    [InlineData("Tenant")]
    [InlineData("DbContext")]
    [InlineData("Http")]
    [InlineData("Provider")]
    [InlineData("Sql")]
    public void No_compiler_type_names_a_concern_that_belongs_to_the_host(string concern)
    {
        var offenders = Compiler.GetTypes()
            .Where(t => t.Name.Contains(concern, StringComparison.OrdinalIgnoreCase))
            .Select(t => t.FullName ?? t.Name)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"'{concern}' belongs to the host, not to the compiler: {string.Join(", ", offenders)}. "
            + "Tool and provider shapes are an adapter in AppBuilder.Generator.");
    }

    [Theory]
    [InlineData("Blueprint")]
    [InlineData("Tool")]
    public void No_cord_type_names_a_concern_that_belongs_above_it(string concern)
    {
        var offenders = Compiler.GetTypes()
            .Where(t => (t.Namespace ?? "").StartsWith("Cordango.Cord", StringComparison.Ordinal))
            .Where(t => t.Name.Contains(concern, StringComparison.OrdinalIgnoreCase))
            .Select(t => t.FullName ?? t.Name)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"'{concern}' belongs above Cord, not inside it: {string.Join(", ", offenders)}. "
            + "Tool and provider shapes are an adapter in AppBuilder.Generator; blueprints are "
            + "Cordango.Definition.Blueprints.");
    }
}

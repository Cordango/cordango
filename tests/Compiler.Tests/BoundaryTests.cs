// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Reflection;
using Cordango.Cord;
using Cordango.TestCorpus;

namespace Cordango.Compiler.Tests;

/// <summary>
/// Rule 0, asserted rather than remembered.
///
/// <para><c>Cordango.Compiler</c> is the portable core, and it now genuinely lives in its own
/// open-source repository. That property has a short half-life: it survives exactly as long as
/// nobody needs "just one thing" from Platform. The failure is silent and cheap at the time — a
/// <c>ProjectReference</c> is one line, and everything still builds — and it is only discovered when
/// somebody tries to build the public repository on its own and finds it welded to a tenant
/// store.</para>
///
/// <para>Restated on 2026-08-20 when Definition and Cord merged into one project. Two rules changed
/// shape and neither was loosened: the csproj bar went from "exactly one project reference" to
/// <b>zero</b>, because there is nothing left to reference; and the type tripwire is now scoped to
/// the <c>Cordango.Cord</c> namespace, because "Blueprint" is a legitimate concern of
/// <c>Cordango.Definition</c> and only ever an intruder in the source model.</para>
///
/// <para>Same argument, and the same shape, as
/// <c>AppBuilder.Api.Tests/ServiceBoundaryTests.cs</c>, which keeps the worker from referencing the
/// API. This one additionally checks the COMPILED assembly, because a csproj assertion alone can be
/// satisfied while a transitive reference smuggles the dependency in anyway.</para>
/// </summary>
public class BoundaryTests
{
    /// <summary>What the compiler may be built on. Not a style preference — every entry here is
    /// either the framework, the schema library the gate evaluates with, or something that library
    /// already brought.</summary>
    /// <remarks>The three Json* entries are ONE permitted package: JsonSchema.Net ships
    /// Json.More.Net and JsonPointer.Net alongside it. They are listed by their real assembly names
    /// because that is what the runtime reports, and because the csproj test below is the one that
    /// counts packages — this list only has to explain what a reference is doing there.</remarks>
    private static readonly string[] Allowed =
    [
        "JsonSchema.Net",
        "Json.More",
        "JsonPointer.Net",
        "netstandard",
        "System.Runtime",
    ];

    /// <summary>The concerns that would end the extraction claim if any of them arrived. Named
    /// explicitly so a failure says WHICH boundary was crossed instead of "unexpected reference".</summary>
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

        // Zero, not "few". The whole claim of the public repository is that this project builds with
        // nothing of ours underneath it.
        var references = csproj.Split("<ProjectReference").Length - 1;
        Assert.True(references == 0, $"expected no ProjectReference, found {references}");

        // Counted, not merely inspected for presence: a package here is a dependency every OSS
        // consumer and every generated application inherits, so the bar is not "is it useful" but
        // "would we ship it".
        var packages = csproj.Split("<PackageReference").Length - 1;
        Assert.True(packages == 1, $"expected exactly one PackageReference, found {packages}");
        Assert.Contains("JsonSchema.Net", csproj);
    }

    /// <summary>
    /// A tripwire for the concerns most likely to leak in as a TYPE rather than as a reference.
    ///
    /// <para>The reference checks catch a csproj edit. They do not catch someone hand-rolling an
    /// <c>AnthropicToolDefinition</c> record inside the compiler with no new dependency at all —
    /// which is the realistic version of this mistake, because the provider shape is just JSON and
    /// copying it looks harmless. The compiler may describe an application; naming a vendor, a
    /// tenant or a transport is where the boundary is.</para>
    /// </summary>
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

    /// <summary>
    /// The same tripwire, one namespace tighter.
    ///
    /// <para>Cord is the model authored source is written in. A blueprint is what a person approved
    /// and a tool shape is how a model was asked — both are real concerns of the layers ABOVE, and
    /// both are exactly the sort of thing that gets copied into the source model because it was
    /// convenient once. <c>Cordango.Definition.Blueprints</c> is where blueprints legitimately
    /// live, which is why this is scoped rather than assembly-wide.</para>
    /// </summary>
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

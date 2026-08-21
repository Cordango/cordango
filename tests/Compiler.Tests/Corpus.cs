// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

// Deliberately NOT Cordango.Compiler.Tests: this file is compiled into more than one test project,
// and a namespace naming one of them would read as a dependency on it from the others.
namespace Cordango.TestCorpus;

/// <summary>
/// The one definition of "the corpus", shared by every suite that has to hold itself against real
/// definitions.
///
/// <para>It lives in this project because the gate is what the corpus was first assembled to protect,
/// but it is <b>source-linked</b> into the other test projects rather than reached through a project
/// reference — a test project referencing another test project drags its fixtures, its
/// <c>[MemberData]</c> and its whole dependency set along with it. The file is small and pure on
/// purpose; linking a file is cheap, and a second private copy of "which files are the corpus" is
/// exactly the drift that lets a new reference app be covered by one suite and silently skipped by
/// another.</para>
///
/// <para>Consumers today: this project (the gate, the union safety net, Cord round-trip and
/// coverage) and <c>AppBuilder.Runtime.Tests</c> in the private platform repository, which links
/// this same file across the submodule boundary rather than keeping a second list.</para>
/// </summary>
public static class Corpus
{
    /// <summary>
    /// The root of the Cordango repository, found by walking up from the test binary until the
    /// schema is underfoot.
    ///
    /// <para>Anchored on the schema FILE rather than on <c>.git</c> or a directory name so it still
    /// works from a build output nested at an unknown depth — and so it still works when this
    /// repository is a submodule, where <c>.git</c> is a file pointing elsewhere and the directory
    /// name is whatever the host chose to mount it as.</para>
    ///
    /// <para>Two shapes, one answer. In a plain clone the schema sits at <c>schemas/</c>; mounted
    /// inside the private monorepo it sits at <c>oss/cordango/schemas/</c>. Either way this returns
    /// the directory that CONTAINS <c>schemas/</c>, so every path below is written once and means
    /// the same thing from both hosts. If it throws while a checkout looks fine, the submodule is
    /// present but empty: initialise it.</para>
    /// </summary>
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (HasSchema(dir.FullName)) return dir.FullName;

            var mounted = Path.Combine(dir.FullName, "oss", "cordango");
            if (HasSchema(mounted)) return mounted;

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Cordango repo root not found from " + AppContext.BaseDirectory +
            " — if oss/cordango exists but is empty, the submodule is not checked out");

        static bool HasSchema(string root) =>
            File.Exists(Path.Combine(root, "schemas", "app-definition.schema.json"));
    }

    /// <summary>The maintained corpus: the hand-authored reference apps + the canonical CRM example.
    /// These are asserted FULLY gate-clean.</summary>
    public static TheoryData<string> MaintainedDefinitions()
    {
        var root = RepoRoot();
        var data = new TheoryData<string>();
        foreach (var p in Directory.GetFiles(Path.Combine(root, "tests", "corpus", "reference"), "*.appdef.json"))
            data.Add(p);
        data.Add(Path.Combine(root, "tests", "corpus", "crm.appdef.json"));
        return data;
    }

    /// <summary>Every definition in the repo except the deliberately-invalid fixture — the widest
    /// net for the structural checks.
    ///
    /// <para>Enumerated directory by directory rather than by recursing <c>tests/corpus/</c>, which
    /// also holds the Budget Planner CordSource specimen: its <c>coverage.json</c> is a report, not
    /// an application, and a recursive glob would feed it to the gate and fail a structural check
    /// with a message about a file nobody thought was a definition.</para></summary>
    public static TheoryData<string> AllDefinitions()
    {
        var root = RepoRoot();
        var data = new TheoryData<string>();
        foreach (var p in Directory.GetFiles(Path.Combine(root, "tests", "corpus", "reference"), "*.appdef.json"))
            data.Add(p);
        data.Add(Path.Combine(root, "tests", "corpus", "crm.appdef.json"));
        foreach (var dir in new[] { "historical", "interactive" })
            foreach (var p in Directory.GetFiles(Path.Combine(root, "tests", "fixtures", dir), "*.json"))
                data.Add(p);
        return data;
    }

    /// <summary>
    /// The maintained corpus plus Budget Planner — every app a semantic authoring layer must be able
    /// to represent.
    ///
    /// <para>Budget Planner is deliberately outside <see cref="MaintainedDefinitions"/>: it is not in
    /// the demo suite and is an exploration artifact rather than a demo app. It is also the hardest
    /// document in the repo — 12 windowed rollups in both directions and at both scales, four
    /// <c>prev()</c> expressions, a series partition, a settings singleton — which makes it the one
    /// app no coverage claim may quietly exclude.</para>
    /// </summary>
    public static TheoryData<string> SemanticCorpus()
    {
        var data = new TheoryData<string>();
        foreach (var p in SemanticPaths()) data.Add(p);
        return data;
    }

    /// <summary>The same set as a plain list. <c>TheoryData</c> is awkward to aggregate over — it is
    /// built to be spread across test cases, not summed — and a coverage report needs the whole
    /// corpus at once. One list, two shapes, so the two can never name different files.</summary>
    public static IReadOnlyList<string> SemanticPaths()
    {
        var root = RepoRoot();
        var paths = new List<string>(
            Directory.GetFiles(Path.Combine(root, "tests", "corpus", "reference"), "*.appdef.json"))
        {
            Path.Combine(root, "tests", "corpus", "crm.appdef.json"),
            Path.Combine(root, "tests", "corpus", "budget-planner.appdef.json"),
        };
        return paths;
    }

    /// <summary>The historical model-generated fixtures. NOT gate-clean and not asserted to be — they
    /// are v1.0-era shapes. They matter to anything claiming to read <i>any</i> definition, because
    /// <c>/editor</c> and refine can produce documents the gate rejects too.</summary>
    public static TheoryData<string> HistoricalDefinitions()
    {
        var dir = Path.Combine(RepoRoot(), "tests", "fixtures", "historical");
        var data = new TheoryData<string>();
        if (!Directory.Exists(dir)) return data;
        foreach (var p in Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories))
            data.Add(p);
        return data;
    }

    /// <summary>The deliberately-invalid fixture: the one document every "does it reject" test
    /// points at, kept out of <see cref="AllDefinitions"/> for obvious reasons.</summary>
    public static string BrokenDefinition() =>
        Path.Combine(RepoRoot(), "tests", "fixtures", "invalid", "broken.appdef.json");
}

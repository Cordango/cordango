// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace Cordango.TestCorpus;

public static class Corpus
{
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

    public static TheoryData<string> MaintainedDefinitions()
    {
        var root = RepoRoot();
        var data = new TheoryData<string>();
        foreach (var p in Directory.GetFiles(Path.Combine(root, "tests", "corpus", "reference"), "*.appdef.json"))
            data.Add(p);
        data.Add(Path.Combine(root, "tests", "corpus", "crm.appdef.json"));
        return data;
    }

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

    public static TheoryData<string> SemanticCorpus()
    {
        var data = new TheoryData<string>();
        foreach (var p in SemanticPaths()) data.Add(p);
        return data;
    }

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

    public static TheoryData<string> HistoricalDefinitions()
    {
        var dir = Path.Combine(RepoRoot(), "tests", "fixtures", "historical");
        var data = new TheoryData<string>();
        if (!Directory.Exists(dir)) return data;
        foreach (var p in Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories))
            data.Add(p);
        return data;
    }

    public static string BrokenDefinition() =>
        Path.Combine(RepoRoot(), "tests", "fixtures", "invalid", "broken.appdef.json");
}

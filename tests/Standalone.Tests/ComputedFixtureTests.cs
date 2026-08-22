// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cordango.Definition;
using Cordango.SourceGen.DotNetVue.Emit;
using Cordango.SourceGen.DotNetVue.Model;

namespace Cordango.Standalone.Tests;

public class ComputedFixtureTests
{
    public static TheoryData<string> Fixtures()
    {
        var data = new TheoryData<string>();
        foreach (var path in System.IO.Directory.EnumerateFiles(FixtureDirectory, "*.json")
                     .OrderBy(p => p, StringComparer.Ordinal))
            data.Add(Path.GetFileName(path));
        return data;
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void The_generator_writes_or_refuses_every_shape_the_fixtures_use(string fileName)
    {
        var fixture = Load(fileName);
        var entity = Entity(fixture["fields"]!.AsObject());

        foreach (var scenario in fixture["cases"]!.AsArray().OfType<JsonObject>())
        {
            var expr = scenario["expr"]!.GetValue<string>();
            var refuses = scenario["generatorRefuses"]?.GetValue<bool>() ?? false;

            var written = ComputedEmitter.Expression(entity, Computed(expr));

            if (refuses)
                Assert.True(written is null,
                    $"{fileName}: the generator now writes `{expr}` as `{written}`. If that is "
                    + "deliberate, drop the generatorRefuses flag from the fixture.");
            else
                Assert.True(written is not null,
                    $"{fileName}: the generator writes nothing for `{expr}`, so this figure would be "
                    + "computed correctly here and the column would stay EMPTY in every generated "
                    + "application.");
        }
    }

    [Fact]
    public void The_fixtures_are_actually_there()
    {
        var files = System.IO.Directory.GetFiles(FixtureDirectory, "*.json");
        Assert.True(files.Length >= 5,
            $"Expected the computed figure fixtures in {FixtureDirectory}; found {files.Length} files. "
            + "They are the only thing keeping this implementation and the platform's from drifting apart, "
            + "and a total that differs between them is the worst failure this project has.");
    }

    private static EntityModel Entity(JsonObject fields)
    {
        var declared = new JsonArray();
        foreach (var (key, spec) in fields)
        {
            declared.Add(new JsonObject
            {
                ["key"] = key,
                ["label"] = key,
                ["type"] = spec?["type"]?.GetValue<string>() ?? "decimal",
                ["required"] = spec?["required"]?.GetValue<bool>() ?? false,
            });
        }

        declared.Add(new JsonObject { ["key"] = ResultKey, ["label"] = "Result", ["type"] = "decimal" });

        return new EntityModel(
            new JsonObject { ["key"] = "row", ["label"] = "Row", ["fields"] = declared },
            "Fixture");
    }

    private static FieldModel Computed(string expr) => new(new JsonObject
    {
        ["key"] = ResultKey,
        ["label"] = "Result",
        ["type"] = "decimal",
        ["computed"] = new JsonObject { ["expr"] = expr },
    }, "row");

    private static JsonObject Load(string fileName) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(FixtureDirectory, fileName)))!.AsObject();

    private const string ResultKey = "the_computed_result";

    private static string FixtureDirectory =>
        Path.Combine(TestPaths.RepoRoot(), "tests", "fixtures", "computed");
}

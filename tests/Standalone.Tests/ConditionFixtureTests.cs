// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Globalization;
using System.Text.Json.Nodes;
using Cordango.SourceGen.DotNetVue.Emit;
using Cordango.Standalone.Conditions;

namespace Cordango.Standalone.Tests;

public class ConditionFixtureTests
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
    public void Decisions_match_the_fixture(string fileName)
    {
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(FixtureDirectory, fileName)))!.AsObject();
        var name = fixture["name"]?.GetValue<string>() ?? fileName;
        var actorId = fixture["actorId"]?.GetValue<string>();
        var now = DateTimeOffset.Parse(
            fixture["now"]?.GetValue<string>() ?? "2026-01-01T00:00:00Z",
            CultureInfo.InvariantCulture);

        var cases = fixture["cases"]!.AsArray();
        Assert.NotEmpty(cases);

        for (var i = 0; i < cases.Count; i++)
        {
            var scenario = cases[i]!.AsObject();
            var record = scenario["record"]!.AsObject();
            var json = scenario["condition"];
            var expected = scenario["expect"]!.GetValue<bool>();

            var why = scenario["why"]?.GetValue<string>();
            var where = $"{fileName} case {i} ({name})"
                + (why is null ? "" : $"\n  {why}")
                + $"\n  condition: {json?.ToJsonString()}"
                + $"\n  record:    {record.ToJsonString()}";

            var actual = ConditionEvaluator.Evaluate(Read(json), record, actorId, now);
            Assert.True(expected == actual, $"expected {expected}, got {actual}\n{where}");
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void The_generator_writes_or_refuses_every_shape_the_fixtures_use(string fileName)
    {
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(FixtureDirectory, fileName)))!.AsObject();

        foreach (var scenario in fixture["cases"]!.AsArray().OfType<JsonObject>())
        {
            var json = scenario["condition"];
            var refuses = scenario["generatorRefuses"]?.GetValue<bool>() ?? false;

            var written = ConditionEmitter.TryEmit(json, out var code);

            if (refuses)
                Assert.False(written,
                    $"{fileName}: the generator now writes {json?.ToJsonString()} as `{code}`. If that is "
                    + "deliberate, drop the generatorRefuses flag from the fixture.");
            else
                Assert.True(written,
                    $"{fileName}: the generator writes nothing for {json?.ToJsonString()}, so this guard "
                    + "would evaluate correctly here and be absent from every generated application.");
        }
    }

    private static Condition? Read(JsonNode? node)
    {
        if (node is not JsonObject leaf) return null;

        if (leaf["all"] is JsonArray all)
            return Composite(all, children => new Condition(All: children));

        if (leaf["any"] is JsonArray any)
            return Composite(any, children => new Condition(Any: children));

        if (leaf["not"] is JsonObject not)
            return Read(not) is { } child ? new Condition(Not: child) : null;

        var op = leaf["operator"]?.GetValue<string>();
        var field = leaf["field"]?.GetValue<string>();
        var path = leaf["path"]?.GetValue<string>();
        var endField = leaf["endField"]?.GetValue<string>();

        return leaf["value"] is JsonArray items
            ? new Condition(Field: field, Path: path, Operator: op, EndField: endField,
                Values: [.. items.Select(Scalar)])
            : new Condition(Field: field, Path: path, Operator: op, EndField: endField,
                Value: leaf["value"] is { } value ? Scalar(value) : null);
    }

    private static Condition? Composite(JsonArray children, Func<List<Condition>, Condition> build)
    {
        var read = children.OfType<JsonObject>().Select(Read).OfType<Condition>().ToList();
        return read.Count == 0 ? null : build(read);
    }

    private static string Scalar(JsonNode? value) => value?.GetValueKind() switch
    {
        null or System.Text.Json.JsonValueKind.Null => "",
        System.Text.Json.JsonValueKind.String => value.GetValue<string>(),
        _ => value.ToJsonString(),
    };

    private static string FixtureDirectory =>
        Path.Combine(TestPaths.RepoRoot(), "tests", "fixtures", "conditions");
}

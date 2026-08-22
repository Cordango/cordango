// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Globalization;
using System.Text.Json.Nodes;
using Cordango.SourceGen.DotNetVue.Emit;
using Cordango.Standalone.Conditions;

namespace Cordango.Standalone.Tests;

/// <summary>
/// The standalone condition language, checked against the decision fixtures that Cordango Platform's
/// own suite also checks its implementation against.
///
/// <para>Same arrangement as the permission fixtures: two implementations that must agree, pinned to
/// one set of hand-written decisions, with no shared code between them. If a case here fails, the
/// interesting question is which side moved — run the platform's <c>ConditionFixtureTests</c> too.
/// Both failing means the fixture is wrong; one failing means an implementation is.</para>
/// </summary>
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

    /// <summary>
    /// Every shape the fixtures use, the generator either writes or refuses — never drops.
    ///
    /// <para>The fixtures pin the two EVALUATORS against each other. They cannot pin the emitter,
    /// because a generated application never sees this JSON — the emitter turns it into C# at build
    /// time. This closes the gap from the other side. A condition the emitter quietly rendered as
    /// <c>null</c> would evaluate correctly in every test above and be ABSENT from every generated
    /// application, which for a guard means the command runs when the definition said it must not.
    /// That is the one bug a person cannot find by reading the application they were given.</para>
    ///
    /// <para>A handful of cases are marked <c>generatorRefuses</c>: shapes so malformed there is
    /// nothing to write. Those must fail to emit, so the build stops instead of shipping the
    /// permissive version.</para>
    /// </summary>
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

    /// <summary>
    /// The fixture's JSON as the runtime's own shape.
    ///
    /// <para>Deliberately a separate reader from <see cref="ConditionEmitter"/>, and deliberately
    /// dumb. Sharing one would make this test assert that the emitter agrees with itself.</para>
    /// </summary>
    private static Condition? Read(JsonNode? node)
    {
        if (node is not JsonObject leaf) return null;

        if (leaf["all"] is JsonArray all)
            return Composite(all, children => new Condition(All: children));

        if (leaf["any"] is JsonArray any)
            return Composite(any, children => new Condition(Any: children));

        if (leaf["not"] is JsonObject not)
            return Read(not) is { } child ? new Condition(Not: child) : null;

        // A leaf with no operator, or with neither a field nor a path, is READ rather than dropped.
        // Dropping it would hand the evaluator a null, which means "no condition" and evaluates to
        // TRUE — so a malformed guard would test as permissive here while the fixture says it must
        // be refused. The typed shape can hold the malformed leaf; the evaluator is what says no.
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

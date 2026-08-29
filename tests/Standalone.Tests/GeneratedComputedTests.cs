// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cordango.Compile;
using Cordango.Definition;
using Cordango.SourceGen;
using Cordango.SourceGen.DotNetVue;
using Cordango.SourceGen.DotNetVue.Model;

namespace Cordango.Standalone.Tests;

/// <summary>
/// The GENERATED code computes the figures the fixtures pin — not an evaluator standing in for it.
///
/// <para><b>This is the half of the contract that belongs to this repository.</b> The fixtures in
/// <c>tests/fixtures/computed/</c> are a specification, and there are two things that have to satisfy
/// it: whatever executes an expression on the platform, and the source code this toolchain writes.
/// The platform's suite asserts the first. This asserts the second, and it is the only test that can:
/// a generated application never sees a fixture, an expression, or a parser — it has arithmetic
/// compiled into it, and the only way to find out what that arithmetic answers is to build it and run
/// it.</para>
///
/// <para><b>Compiling is not computing, and emitting is not compiling.</b>
/// <c>ComputedFixtureTests</c> checks that the emitter produces SOMETHING for every shape. It cannot
/// check that the something is valid C#, and it cannot check what it returns. Both gaps were real:
/// <c>and</c> over a comparison emitted <c>&amp;&amp;</c> on a <c>bool?</c>, which is not valid C# at
/// all, and no test noticed because no corpus application has a computed field that combines a
/// comparison with boolean logic.</para>
///
/// <para>The application is DERIVED from the fixture files rather than checked in beside them. Add a
/// case and it gets a computed field, a generated method and an assertion with nothing to keep in
/// step by hand — and it lives nowhere near <c>tests/corpus/</c>, so it cannot disturb the corpus
/// counts.</para>
/// </summary>
public class GeneratedComputedTests
{
    private static bool Skipped => Environment.GetEnvironmentVariable("CORDANGO_SKIP_SDK_TESTS") == "1";

    public static TheoryData<string> Fixtures()
    {
        var data = new TheoryData<string>();
        foreach (var path in FixtureFiles()) data.Add(Path.GetFileName(path));
        return data;
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task Generated_code_computes_the_fixture_answers(string fileName)
    {
        if (Skipped) return;

        var built = await Built();
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(FixtureDirectory, fileName)))!.AsObject();
        var name = fixture["name"]?.GetValue<string>() ?? fileName;

        var cases = fixture["cases"]!.AsArray();
        for (var i = 0; i < cases.Count; i++)
        {
            var scenario = cases[i]!.AsObject();
            var caseKey = CaseKey(fileName, i);
            var methodName = Naming.Pascal(caseKey);
            var expr = scenario["expr"]!.GetValue<string>();
            var record = scenario["record"]!.AsObject();
            var expected = scenario["expect"];

            var why = scenario["why"]?.GetValue<string>();
            var where = $"{fileName} case {i} ({name})"
                + (why is null ? "" : $"\n  {why}")
                + $"\n  expr:   {expr}"
                + $"\n  record: {record.ToJsonString()}"
                + $"\n  method: {built.ComputedType.Name}.{methodName}";

            // A case whose expression the generator refuses has no method, and that is the fixture
            // saying so rather than this test having lost one.
            if (scenario["generatorRefuses"]?.GetValue<bool>() == true)
            {
                Assert.False(built.Methods.ContainsKey(methodName),
                    $"the fixture marks this generatorRefuses, but a method was generated.\n{where}");
                continue;
            }

            Assert.True(built.Methods.TryGetValue(methodName, out var method),
                $"no computed method was generated for this case, so the column would stay empty in a "
                + $"real application.\n{where}\n  generated: "
                + string.Join(", ", built.Methods.Keys.Take(8)) + " …");

            var instance = Activator.CreateInstance(built.EntityType)!;
            foreach (var field in built.BaseFields)
                Set(instance, field, record[field.Key], where);

            var actual = method!.Invoke(null, [instance]);

            switch (expected)
            {
                case null:
                    Assert.True(actual is null, $"expected unknown, got {Show(actual)}.\n{where}");
                    break;

                case JsonValue flag when flag.GetValueKind() is JsonValueKind.True or JsonValueKind.False:
                    Assert.True(actual is bool,
                        $"expected a boolean, the generated method returned {Show(actual)} "
                        + $"({method.ReturnType}).\n{where}");
                    Assert.True(flag.GetValue<bool>().Equals(actual),
                        $"expected {flag.GetValue<bool>()}, got {Show(actual)}.\n{where}");
                    break;

                case JsonValue text when text.GetValueKind() is JsonValueKind.String:
                    Assert.True(actual is DateOnly,
                        $"expected a date, the generated method returned {Show(actual)} "
                        + $"({method.ReturnType}).\n{where}");
                    Assert.True(
                        DateOnly.ParseExact(text.GetValue<string>(), "yyyy-MM-dd").Equals(actual),
                        $"expected {text.GetValue<string>()}, got {Show(actual)}.\n{where}");
                    break;

                case JsonValue number:
                    Assert.True(actual is decimal,
                        $"expected a number, the generated method returned {Show(actual)} "
                        + $"({method.ReturnType}).\n{where}");
                    Assert.True(number.GetValue<decimal>() == (decimal)actual!,
                        $"expected {number.GetValue<decimal>()}, got {Show(actual)}.\n{where}");
                    break;

                default:
                    Assert.Fail($"`expect` must be a number, an ISO date string, true, false, or null.\n{where}");
                    break;
            }
        }
    }

    /// <summary>
    /// The generated application compiles at all.
    ///
    /// <para>Stated as its own test so that a build failure reports as a build failure rather than as
    /// fifty-eight identical arithmetic failures with a stack trace from <c>Assembly.LoadFrom</c>.</para>
    /// </summary>
    [Fact]
    public async Task The_fixture_application_compiles()
    {
        if (Skipped) return;

        var built = await Built();
        Assert.NotNull(built.EntityType);
        Assert.NotEmpty(built.Methods);
    }

    // ---- the application, derived from the fixtures ----------------------------------------------

    /// <summary>
    /// One entity carrying every field the fixtures declare and one computed field per case.
    ///
    /// <para>The field TYPE of each computed column comes from the language's own type inference
    /// rather than from the fixture's <c>expect</c>, because <c>expect: null</c> says nothing about
    /// whether the answer would have been a number or a boolean — and the Gate refuses a boolean
    /// expression stored in a decimal column, correctly.</para>
    /// </summary>
    private static JsonObject Definition(out List<FieldSpec> baseFields, out List<string> caseKeys)
    {
        var declared = new SortedDictionary<string, (string Type, bool Required)>(StringComparer.Ordinal);
        var computed = new List<(string Key, string Expr)>();

        foreach (var path in FixtureFiles())
        {
            var fixture = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var fileName = Path.GetFileName(path);

            foreach (var (key, spec) in fixture["fields"]!.AsObject())
            {
                var type = spec?["type"]?.GetValue<string>() ?? "decimal";
                var required = spec?["required"]?.GetValue<bool>() ?? false;

                if (declared.TryGetValue(key, out var already) && already != (type, required))
                    Assert.Fail(
                        $"the fixtures disagree about '{key}': {already.Type} in one file and {type} in "
                        + $"{fileName}. One entity carries them all, so a field must mean one thing.");

                declared[key] = (type, required);
            }

            var cases = fixture["cases"]!.AsArray();
            for (var i = 0; i < cases.Count; i++)
            {
                var scenario = cases[i]!.AsObject();
                if (scenario["generatorRefuses"]?.GetValue<bool>() == true) continue;
                computed.Add((CaseKey(fileName, i), scenario["expr"]!.GetValue<string>()));
            }
        }

        var fields = new JsonArray
        {
            // A display field, so the generated screens have something to title a record with.
            new JsonObject { ["key"] = "name", ["label"] = "Name", ["type"] = "text" },
        };

        foreach (var (key, spec) in declared)
            fields.Add(new JsonObject
            {
                ["key"] = key,
                ["label"] = Label(key),
                ["type"] = spec.Type,
                ["required"] = spec.Required,
            });

        foreach (var (key, expr) in computed)
        {
            var kind = ComputedExpr.Validate(expr, ident => declared.TryGetValue(ident, out var s)
                ? s.Type switch
                {
                    "boolean" => ComputedValueKind.Boolean,
                    "date" or "datetime" => ComputedValueKind.Date,
                    _ => ComputedValueKind.Number,
                }
                : ComputedValueKind.Number).ResultKind;

            Assert.True(
                kind is ComputedValueKind.Number or ComputedValueKind.Boolean or ComputedValueKind.Date,
                $"'{expr}' infers as {kind?.ToString() ?? "nothing"}, which this harness has no column "
                + "type for. Add one, or drop the case.");

            fields.Add(new JsonObject
            {
                ["key"] = key,
                ["label"] = Label(key),
                ["type"] = kind switch
                {
                    ComputedValueKind.Boolean => "boolean",
                    ComputedValueKind.Date => "date",
                    _ => "decimal",
                },
                ["computed"] = new JsonObject { ["expr"] = expr },
            });
        }

        baseFields = [.. declared.Select(d => new FieldSpec(d.Key, d.Value.Type))];
        caseKeys = [.. computed.Select(c => c.Key)];

        return new JsonObject
        {
            ["schemaVersion"] = "2.0",
            ["key"] = AppKey,
            ["name"] = "Computed Fixtures",
            ["version"] = "1.0.0",
            ["entities"] = new JsonArray
            {
                new JsonObject
                {
                    ["key"] = "row",
                    ["label"] = "Row",
                    ["labelPlural"] = "Rows",
                    ["displayField"] = "name",
                    ["fields"] = fields,
                },
            },
        };
    }

    /// <summary>Built once and shared. Fifty-eight cases across five files is one
    /// <c>dotnet build</c>, not five.</summary>
    private static async Task<BuiltApp> Built()
    {
        await Gate.WaitAsync();
        try
        {
            if (_built is not null) return _built;

            var definition = Definition(out var baseFields, out _);

            var outcome = CandidateValidator.Run(
                definition, AppKey, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            Assert.True(outcome.Manifest is not null,
                "the fixture application did not compile: " + string.Join("; ", outcome.Errors));

            var artifact = new CompiledAppArtifact(
                outcome.Definition!.AsObject(), outcome.Manifest!,
                outcome.Hash ?? "unhashed", new CompilerInfo("test", "1"));

            var result = new DotNetVueGenerator().Generate(new GenerateRequest(artifact, new JsonObject
            {
                ["allowIncomplete"] = true,
                ["seed"] = 42,
            }));

            Assert.True(result.Ok,
                "generation refused the fixture application:\n"
                + string.Join("\n", result.Errors.Select(e => e.Code + ": " + e.Message)));

            // NOT disposed: loading an assembly holds its file open for the life of the process, so
            // deleting the directory underneath it fails. Left in TEMP for the operating system.
            var root = Path.Combine(Path.GetTempPath(), "cordango-computed-" + Guid.NewGuid().ToString("n")[..8]);
            foreach (var file in result.Files)
            {
                var target = Path.Combine(root, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, new UTF8Encoding(false).GetBytes(file.Content));
            }

            await GeneratedApplicationTests.Build(new GeneratedApplicationTests.Materialised { Root = root });

            var assembly = Assembly.LoadFrom(Path.Combine(
                root, "api", "bin", "Release", "net10.0", Namespace + ".Api.dll"));

            var entity = assembly.GetType($"{Namespace}.Entities.Row", throwOnError: true)!;
            var computedType = assembly.GetType($"{Namespace}.Computed.RowComputed", throwOnError: true)!;

            // The model the emitter itself would build, so property names are ITS answer rather than
            // this test's guess at its casing rules.
            var model = new EntityModel(
                (JsonObject)outcome.Manifest!["entities"]!.AsArray()
                    .First(e => e!["key"]!.GetValue<string>() == "row")!, Namespace);

            var methods = computedType
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.GetParameters() is [{ } p] && p.ParameterType == entity)
                .ToDictionary(m => m.Name, m => m, StringComparer.Ordinal);

            // Keyed by the METHOD name, and looked up through `Naming.Pascal` — the very function
            // `BackendEmitter` used to write it. Going the other way is not possible: `case_01_00`
            // pascalises to `Case0100` and the separators are gone for good.
            _built = new BuiltApp(
                entity,
                computedType,
                [.. baseFields.Select(f => f with { PropertyName = model.Field(f.Key)?.PropertyName ?? f.Key })],
                methods);

            return _built;
        }
        finally
        {
            Gate.Release();
        }
    }

    // ---- setting one value on the generated record -----------------------------------------------

    private static void Set(object record, FieldSpec field, JsonNode? value, string where)
    {
        var property = record.GetType().GetProperty(field.PropertyName);
        Assert.True(property is not null,
            $"the generated entity has no property '{field.PropertyName}' for field '{field.Key}'.\n{where}");

        // Absent means nobody filled it in, which is the subject of most of these fixtures. Leave the
        // property at its default rather than writing a zero over it.
        if (value is null) return;

        object? converted = field.Type switch
        {
            "integer" => (long?)value.GetValue<decimal>(),
            "decimal" or "money" => value.GetValue<decimal>(),
            "boolean" => value.GetValue<bool>(),
            "date" => DateOnly.ParseExact(value.GetValue<string>(), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            "datetime" => DateTimeOffset.Parse(value.GetValue<string>(), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
            _ => value.GetValue<string>(),
        };

        property!.SetValue(record, converted);
    }

    // ---- names --------------------------------------------------------------------------------

    /// <summary>A field key per case: the file's number and the case's index, so a failure names the
    /// fixture it came from.</summary>
    private static string CaseKey(string fileName, int index) =>
        $"case_{fileName[..2]}_{index:00}";

    private static string Label(string key) =>
        string.Join(' ', key.Split('_').Select(w => char.ToUpperInvariant(w[0]) + w[1..]));

    private static string Show(object? value) => value switch
    {
        null => "unknown",
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "?",
    };

    private static IEnumerable<string> FixtureFiles() =>
        System.IO.Directory.EnumerateFiles(FixtureDirectory, "*.json")
            .OrderBy(p => p, StringComparer.Ordinal);

    private static string FixtureDirectory =>
        Path.Combine(TestPaths.RepoRoot(), "tests", "fixtures", "computed");

    private const string AppKey = "computed_fixtures";
    private const string Namespace = "ComputedFixtures";

    internal sealed record FieldSpec(string Key, string Type)
    {
        public string PropertyName { get; init; } = Key;
    }

    private sealed record BuiltApp(
        Type EntityType,
        Type ComputedType,
        IReadOnlyList<FieldSpec> BaseFields,
        IReadOnlyDictionary<string, MethodInfo> Methods);

    private static BuiltApp? _built;

    private static readonly SemaphoreSlim Gate = new(1, 1);
}

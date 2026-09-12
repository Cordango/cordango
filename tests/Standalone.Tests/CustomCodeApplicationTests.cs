// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text;
using System.Text.Json.Nodes;
using Cordango.Compile;
using Cordango.SourceGen;
using Cordango.SourceGen.DotNet;

namespace Cordango.Standalone.Tests;

/// <summary>
/// The only proof that matters: an application with custom code in it, compiled by the real C#
/// compiler.
///
/// <para><b>Everything upstream of this is a claim.</b> The scanner reads a signature out of text
/// without compiling anything; the emitter writes a call site from what the scanner said. Both can
/// be confidently wrong in the same direction, and no test that reads the emitter's strings would
/// notice. This one hands the result to the compiler that has the final say — and has already caught
/// four defects nothing else did: adapter methods named after the interface rather than its member,
/// entity types not in scope, attributes not in scope, and a class declaration with its brace in the
/// wrong place.</para>
///
/// <para><b>Deliberately not in the corpus.</b> <c>SchemaConformanceTests</c> and
/// <c>GeneratedApplicationTests</c> carry exact counts and an explicit list of applications; adding
/// one there would fail tests in files that have nothing to do with custom code. The fixture is
/// built here, from an existing corpus application plus the code under test.</para>
/// </summary>
public class CustomCodeApplicationTests
{
    private static bool Skipped => Environment.GetEnvironmentVariable("CORDANGO_SKIP_SDK_TESTS") == "1";

    private const string Sources = """
        namespace Expenses.Custom;

        using Cordango.Standalone.Http;

        [CordangoFunctions]
        public static class Money
        {
            [CordangoFunction("to_cents", Description = "Whole cents, halves to even.")]
            public static decimal? ToCents(decimal? value) =>
                value is null ? null : decimal.Round(value.Value, 2, MidpointRounding.ToEven);

            [CordangoFunction("is_large", Description = "Whether a claim needs a second pair of eyes.")]
            public static bool? IsLarge(decimal? value, string? category) =>
                value is null ? null : value > 1000m || category == "travel";
        }

        [CordangoHooks]
        public sealed class ClaimRules
        {
            [BeforeCreate]
            public Task OnCreate(ExpenseClaim record, RecordContext context, CancellationToken ct) =>
                Task.CompletedTask;

            [BeforeUpdate(Stage = HookStage.AfterComputed)]
            public Task OnUpdate(ExpenseClaim record, ExpenseClaim before, RecordContext context,
                CancellationToken ct)
            {
                if (record.Id != before.Id) throw new RecordException("claim.identity", "Ids do not move.");
                return Task.CompletedTask;
            }

            [AfterCreate]
            public Task AfterCreate(ExpenseClaim record, RecordContext context, CancellationToken ct) =>
                Task.CompletedTask;

            [AfterUpdate]
            public Task AfterUpdate(ExpenseClaim record, ExpenseClaim before, RecordContext context,
                CancellationToken ct) => Task.CompletedTask;

            [BeforeDelete]
            public Task BeforeDelete(ExpenseClaim record, RecordContext context, CancellationToken ct) =>
                Task.CompletedTask;

            [AfterDelete]
            public Task AfterDelete(ExpenseClaim record, RecordContext context, CancellationToken ct) =>
                Task.CompletedTask;
        }
        """;

    /// <summary>
    /// The expenses application, with a custom function wired into a real computed field.
    ///
    /// <para>The field is added rather than reused so that the expression is definitely the one under
    /// test, and typed <c>decimal</c> because <c>to_cents</c> answers a number.</para>
    /// </summary>
    private static (CustomSourceBundle Bundle, JsonObject Definition) Fixture()
    {
        var path = Path.Combine(TestPaths.RepoRoot(), "tests", "corpus", "reference", "expenses.appdef.json");
        var definition = JsonNode.Parse(File.ReadAllText(path))!.AsObject();

        var claim = (definition["entities"] as JsonArray)!
            .OfType<JsonObject>()
            .First(e => (string?)e["key"] == "expense_claim");

        var amount = (claim["fields"] as JsonArray)!
            .OfType<JsonObject>()
            .First(f => (string?)f["type"] is "money" or "decimal");

        (claim["fields"] as JsonArray)!.Add(new JsonObject
        {
            ["key"] = "amount_in_cents",
            ["label"] = "Amount in cents",
            ["type"] = "decimal",
            ["computed"] = new JsonObject
            {
                ["expr"] = $"custom.to_cents({(string?)amount["key"]})",
            },
        });

        var files = new Dictionary<string, string>(StringComparer.Ordinal) { ["Money.cs"] = Sources };
        var scan = new DotNetVueGenerator().Scan(new CustomCodeContext(
            "expenses", "Expenses",
            [.. (definition["entities"] as JsonArray)!.OfType<JsonObject>()
                .Select(e => new CustomEntity((string?)e["key"] ?? ""))],
            files));

        Assert.Empty(scan.Errors);

        return (new CustomSourceBundle("dotnet", FixtureHash, files), definition);
    }

    /// <summary>
    /// A stand-in hash.
    ///
    /// <para>What the generator checks is that the definition and the bundle AGREE, and both sides
    /// here come from this one value, so the framing algorithm is beside the point. That algorithm
    /// is what <c>CustomSourceTests</c> pins, over real files on a real disk — repeating it here
    /// would only couple this test to the CLI, which the generator deliberately knows nothing
    /// about.</para>
    /// </summary>
    private const string FixtureHash = "sha256:" + "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static GenerateResult Generate()
    {
        var (bundle, definition) = Fixture();

        // Grafted BEFORE the document is validated, exactly as the pipeline does it: an expression
        // calling custom.to_cents cannot be type-checked against a definition that does not yet say
        // what custom.to_cents takes. Doing this afterwards is how the fixture first failed.
        var scan = new DotNetVueGenerator().Scan(new CustomCodeContext(
            "expenses", "Expenses",
            [.. (definition["entities"] as JsonArray)!.OfType<JsonObject>()
                .Select(e => new CustomEntity((string?)e["key"] ?? ""))],
            bundle.Files));

        var section = scan.Metadata;
        section["hash"] = bundle.Hash;
        definition["custom"] = section;

        var outcome = CandidateValidator.Run(
            definition, "expenses", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.True(outcome.Manifest is not null,
            "the fixture did not compile: " + string.Join("; ", outcome.Errors));

        var artifact = new CompiledAppArtifact(
            outcome.Definition!.AsObject(), outcome.Manifest!, outcome.Hash ?? "unhashed",
            new CompilerInfo("test", "1"));

        return new DotNetVueGenerator().Generate(new GenerateRequest(
            artifact, new JsonObject { ["allowIncomplete"] = true, ["seed"] = 42 }, bundle));
    }

    [Fact]
    public async Task An_application_with_custom_code_compiles()
    {
        if (Skipped) return;

        var result = Generate();
        Assert.True(result.Ok, string.Join("\n", result.Errors.Select(e => e.Code + ": " + e.Message)));

        var root = Path.Combine(Path.GetTempPath(), "cordango-custom-app-" + Guid.NewGuid().ToString("n")[..8]);

        foreach (var file in result.Files)
        {
            var target = Path.Combine(root, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, new UTF8Encoding(false).GetBytes(file.Content));
        }

        using var app = new GeneratedApplicationTests.Materialised { Root = root };
        await GeneratedApplicationTests.Build(app);
    }

    [Fact]
    public void The_sources_and_every_adapter_are_emitted()
    {
        var paths = Generate().Files.Select(f => f.RelativePath).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("api/Custom/Money.cs", paths);

        foreach (var adapter in new[]
        {
            "CustomClaimRulesOnCreate", "CustomClaimRulesOnUpdate",
            "CustomClaimRulesAfterCreate", "CustomClaimRulesAfterUpdate",
            "CustomClaimRulesBeforeDelete", "CustomClaimRulesAfterDelete",
        })
        {
            Assert.Contains($"api/Hooks/{adapter}.cs", paths);
        }
    }

    [Fact]
    public void The_computed_field_calls_the_custom_function()
    {
        var computed = Generate().Files
            .First(f => f.RelativePath == "api/Computed/ExpenseClaimComputed.cs");

        Assert.Contains("global::Expenses.Custom.Money.ToCents(", computed.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void A_hook_that_asked_to_run_after_the_figures_is_registered_after_them()
    {
        var setup = Generate().Files.First(f => f.RelativePath == "api/AppSetup.cs").Content;

        var computed = setup.IndexOf("ExpenseClaimComputedFields", StringComparison.Ordinal);
        var before = setup.IndexOf("CustomClaimRulesOnCreate", StringComparison.Ordinal);
        var after = setup.IndexOf("CustomClaimRulesOnUpdate", StringComparison.Ordinal);

        Assert.True(before >= 0 && after >= 0 && computed >= 0, "the registrations are not all there");
        Assert.True(before < computed, "a before_computed hook must be registered before the figures");
        Assert.True(after > computed, "an after_computed hook must be registered after the figures");
    }
}

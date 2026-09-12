// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.SourceGen;
using Cordango.SourceGen.DotNet.Custom;

namespace Cordango.Standalone.Tests;

/// <summary>
/// What the scanner accepts, what it refuses, and what it says about the refusal.
///
/// <para>The messages are asserted rather than the codes alone. A refusal somebody cannot act on is
/// the failure mode this whole surface exists to avoid: the rules are deliberately narrow, so every
/// one of them has to say what to write instead.</para>
/// </summary>
public class CustomCodeScanTests
{
    private const string Head = "namespace ExpenseClaims.Custom;\n\n";

    private static CustomScanResult Scan(string body, params string[] entities)
    {
        var context = new CustomCodeContext(
            "expense_claims",
            "Expense Claims",
            [.. entities.Select(e => new CustomEntity(e))],
            new Dictionary<string, string>(StringComparer.Ordinal) { ["Pricing.cs"] = Head + body });

        return new CSharpScanner().Scan(context);
    }

    private static JsonArray Functions(CustomScanResult result) =>
        result.Metadata["functions"]?.AsArray() ?? [];

    private static JsonArray Hooks(CustomScanResult result) =>
        result.Metadata["hooks"]?.AsArray() ?? [];

    private static string Messages(CustomScanResult result) =>
        string.Join(" | ", result.Errors.Select(e => e.Message));

    [Fact]
    public void A_function_becomes_its_signature()
    {
        var result = Scan("""
            [CordangoFunctions]
            public static class Pricing
            {
                [CordangoFunction("discount", Description = "Tier discount.")]
                public static decimal? Discount(decimal? total, string? tier) => total;
            }
            """);

        Assert.Empty(result.Errors);

        var fn = Functions(result).Single()!.AsObject();
        Assert.Equal("discount", (string?)fn["name"]);
        Assert.Equal("number", (string?)fn["returns"]);
        Assert.Equal("Tier discount.", (string?)fn["description"]);
        Assert.Equal("Pricing", (string?)fn["source"]!["type"]);
        Assert.Equal("Discount", (string?)fn["source"]!["method"]);

        var parameters = fn["params"]!.AsArray();
        Assert.Equal(2, parameters.Count);
        Assert.Equal("total", (string?)parameters[0]!["name"]);
        Assert.Equal("number", (string?)parameters[0]!["kind"]);
        Assert.Equal("text", (string?)parameters[1]!["kind"]);
    }

    [Theory]
    [InlineData("decimal", "number")]
    [InlineData("int?", "number")]
    [InlineData("long?", "number")]
    [InlineData("DateOnly?", "date")]
    [InlineData("double?", "number")]
    public void A_type_a_formula_cannot_carry_is_refused_by_name(string written, string _)
    {
        var result = Scan($$"""
            [CordangoFunctions]
            public static class Pricing
            {
                [CordangoFunction("discount")]
                public static decimal? Discount({{written}} total) => total;
            }
            """);

        Assert.Contains(result.Errors, e => e.Code == CustomCodeCodes.Type);
        Assert.Contains("decimal?, bool? or string?", Messages(result), StringComparison.Ordinal);
        Assert.Empty(Functions(result));
    }

    [Fact]
    public void A_date_is_refused_and_says_so_rather_than_going_quiet()
    {
        var result = Scan("""
            [CordangoFunctions]
            public static class Pricing
            {
                [CordangoFunction("age")]
                public static decimal? Age(DateOnly? on) => null;
            }
            """);

        Assert.Contains("A date is not offered yet", Messages(result), StringComparison.Ordinal);
    }

    [Fact]
    public void Reading_the_clock_is_refused()
    {
        var result = Scan("""
            [CordangoFunctions]
            public static class Pricing
            {
                [CordangoFunction("stale")]
                public static bool? Stale(decimal? days) => System.DateTime.UtcNow.Hour > 0;
            }
            """);

        Assert.Contains(result.Errors, e => e.Code == CustomCodeCodes.Determinism);
        Assert.Contains("right once and wrong afterwards", Messages(result), StringComparison.Ordinal);
    }

    [Fact]
    public void The_clock_in_a_comment_is_not_a_call()
    {
        var result = Scan("""
            [CordangoFunctions]
            public static class Pricing
            {
                // never use DateTime.UtcNow in here
                [CordangoFunction("double_it")]
                public static decimal? DoubleIt(decimal? total) => total * 2;
            }
            """);

        Assert.Empty(result.Errors);
        Assert.Single(Functions(result));
    }

    [Fact]
    public void An_async_function_is_refused()
    {
        var result = Scan("""
            [CordangoFunctions]
            public static class Pricing
            {
                [CordangoFunction("slow")]
                public static async Task<decimal?> Slow(decimal? total) => total;
            }
            """);

        Assert.Contains("has to answer", Messages(result), StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_that_is_not_a_definition_key_is_refused()
    {
        var result = Scan("""
            [CordangoFunctions]
            public static class Pricing
            {
                [CordangoFunction("Discount")]
                public static decimal? Discount(decimal? total) => total;
            }
            """);

        Assert.Contains("lower case letters", Messages(result), StringComparison.Ordinal);
    }

    [Fact]
    public void One_name_declared_twice_is_refused()
    {
        var result = Scan("""
            [CordangoFunctions]
            public static class Pricing
            {
                [CordangoFunction("discount")]
                public static decimal? One(decimal? total) => total;

                [CordangoFunction("discount")]
                public static decimal? Two(decimal? total) => total;
            }
            """);

        Assert.Contains(result.Errors, e => e.Code == CustomCodeCodes.Duplicate);
        Assert.Single(Functions(result));
    }

    [Fact]
    public void An_attributed_method_in_an_unmarked_class_is_not_silence()
    {
        var result = Scan("""
            public static class Pricing
            {
                [CordangoFunction("discount")]
                public static decimal? Discount(decimal? total) => total;
            }
            """);

        Assert.Contains("[CordangoFunctions]", Messages(result), StringComparison.Ordinal);
        Assert.Empty(Functions(result));
    }

    [Fact]
    public void An_ordinary_helper_class_is_left_alone()
    {
        var result = Scan("""
            internal static class PricingMath
            {
                public static decimal Round(decimal v) => v;
            }
            """);

        Assert.Empty(result.Errors);
        Assert.Empty(Functions(result));
    }

    [Fact]
    public void The_wrong_namespace_is_refused_and_names_the_right_one()
    {
        var context = new CustomCodeContext(
            "expense_claims", "Expense Claims", [],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Pricing.cs"] = "namespace Whatever;\n\npublic static class Pricing { }\n",
            });

        var result = new CSharpScanner().Scan(context);

        Assert.Contains("ExpenseClaims.Custom", Messages(result), StringComparison.Ordinal);
    }

    [Fact]
    public void A_preprocessor_directive_is_refused_because_the_file_is_read_as_text()
    {
        var result = Scan("""
            #if DEBUG
            [CordangoFunctions]
            public static class Pricing { }
            #endif
            """);

        Assert.Contains("preprocessor directive", Messages(result), StringComparison.Ordinal);
    }

    [Fact]
    public void A_create_hook_resolves_its_entity_from_the_generated_type_name()
    {
        var result = Scan("""
            [CordangoHooks]
            public sealed class Rules
            {
                [BeforeCreate]
                public Task Stamp(ExpenseClaim record, RecordContext ctx, CancellationToken ct) => Task.CompletedTask;
            }
            """, "expense_claim");

        Assert.Empty(result.Errors);

        var hook = Hooks(result).Single()!.AsObject();
        Assert.Equal("expense_claim", (string?)hook["entity"]);
        Assert.Equal("before_create", (string?)hook["event"]);
        Assert.Equal("before_computed", (string?)hook["stage"]);
    }

    [Fact]
    public void An_update_hook_without_the_previous_row_is_refused()
    {
        var result = Scan("""
            [CordangoHooks]
            public sealed class Rules
            {
                [BeforeUpdate]
                public Task Check(ExpenseClaim record, RecordContext ctx, CancellationToken ct) => Task.CompletedTask;
            }
            """, "expense_claim");

        Assert.Contains(result.Errors, e => e.Code == CustomCodeCodes.HookSignature);
        Assert.Contains("T before", Messages(result), StringComparison.Ordinal);
    }

    [Fact]
    public void An_update_hook_with_both_versions_is_accepted()
    {
        var result = Scan("""
            [CordangoHooks]
            public sealed class Rules
            {
                [AfterUpdate]
                public Task Log(ExpenseClaim record, ExpenseClaim before, RecordContext ctx, CancellationToken ct) => Task.CompletedTask;
            }
            """, "expense_claim");

        Assert.Empty(result.Errors);
        Assert.Equal("after_update", (string?)Hooks(result).Single()!["event"]);
        Assert.Null(Hooks(result).Single()!["stage"]);
    }

    [Fact]
    public void A_hook_on_a_record_the_app_does_not_have_is_refused()
    {
        var result = Scan("""
            [CordangoHooks]
            public sealed class Rules
            {
                [BeforeCreate]
                public Task Stamp(Invoice record, RecordContext ctx, CancellationToken ct) => Task.CompletedTask;
            }
            """, "expense_claim");

        Assert.Contains("not one of this application's records", Messages(result), StringComparison.Ordinal);
    }

    [Fact]
    public void An_authored_stage_is_carried_through()
    {
        var result = Scan("""
            [CordangoHooks]
            public sealed class Rules
            {
                [BeforeCreate(Stage = HookStage.AfterComputed)]
                public Task Read(ExpenseClaim record, RecordContext ctx, CancellationToken ct) => Task.CompletedTask;
            }
            """, "expense_claim");

        Assert.Empty(result.Errors);
        Assert.Equal("after_computed", (string?)Hooks(result).Single()!["stage"]);
    }

    [Fact]
    public void Reaching_outward_warns_rather_than_refusing()
    {
        var result = Scan("""
            [CordangoFunctions]
            public static class Pricing
            {
                [CordangoFunction("rate")]
                public static decimal? Rate(decimal? total) => Environment.ProcessorCount > 0 ? total : null;
            }
            """);

        Assert.Contains(result.Warnings, w => w.Code == CustomCodeCodes.SideEffect);
    }

    [Fact]
    public void Hooks_come_out_in_declaration_order_because_that_is_run_order()
    {
        var result = Scan("""
            [CordangoHooks]
            public sealed class Rules
            {
                [BeforeCreate]
                public Task First(ExpenseClaim record, RecordContext ctx, CancellationToken ct) => Task.CompletedTask;

                [BeforeCreate]
                public Task Second(ExpenseClaim record, RecordContext ctx, CancellationToken ct) => Task.CompletedTask;
            }
            """, "expense_claim");

        Assert.Empty(result.Errors);
        Assert.Equal(
            ["First", "Second"],
            Hooks(result).Select(h => (string)h!["source"]!["method"]!).ToArray());
    }

    [Fact]
    public void Nothing_at_all_is_a_clean_empty_answer()
    {
        var context = new CustomCodeContext(
            "expense_claims", "Expense Claims", [],
            new Dictionary<string, string>(StringComparer.Ordinal));

        var result = new CSharpScanner().Scan(context);

        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
        Assert.Equal("dotnet", (string?)result.Metadata["language"]);
    }
}

// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.Definition;

namespace Cordango.Compiler.Tests;

/// <summary>
/// <c>custom.name(...)</c>: how it parses, how it type-checks, and — most of all — how it stays out
/// of the two context-free collectors.
/// </summary>
public class CustomCallTests
{
    private static readonly Func<string, ComputedValueKind?> Fields = name => name switch
    {
        "total" => ComputedValueKind.Number,
        "tier" => ComputedValueKind.Text,
        "paid" => ComputedValueKind.Boolean,
        "custom" => ComputedValueKind.Number,
        "custom.owner" => ComputedValueKind.Number,
        _ => null,
    };

    private static readonly Func<string, CustomSignature?> Signatures = name => name switch
    {
        "discount" => new CustomSignature(
            [ComputedValueKind.Number, ComputedValueKind.Text], ComputedValueKind.Number),
        "flag" => new CustomSignature([], ComputedValueKind.Boolean),
        _ => null,
    };

    private static ComputedExprValidation Check(string expr) =>
        ComputedExpr.Validate(expr, Fields, customSignature: Signatures);

    [Fact]
    public void A_declared_call_type_checks_and_answers_its_declared_kind()
    {
        var result = Check("custom.discount(total, tier)");

        Assert.Null(result.Error);
        Assert.Equal(ComputedValueKind.Number, result.ResultKind);
    }

    [Fact]
    public void A_call_with_no_arguments_is_fine()
    {
        Assert.Null(Check("custom.flag()").Error);
    }

    [Fact]
    public void A_call_composes_with_ordinary_arithmetic()
    {
        var result = Check("total - custom.discount(total, tier)");

        Assert.Null(result.Error);
        Assert.Equal(ComputedValueKind.Number, result.ResultKind);
    }

    [Fact]
    public void A_name_nothing_declares_says_where_such_names_come_from()
    {
        var result = Check("custom.rebate(total)");

        Assert.NotNull(result.Error);
        Assert.Contains("not a function this application declares", result.Error, StringComparison.Ordinal);
        Assert.Contains("custom/", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_wrong_number_of_arguments_is_refused()
    {
        var result = Check("custom.discount(total)");

        Assert.NotNull(result.Error);
        Assert.Contains("takes 2 arguments, not 1 argument", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_argument_of_the_wrong_kind_names_the_position()
    {
        var result = Check("custom.discount(tier, tier)");

        Assert.NotNull(result.Error);
        Assert.Contains("argument 1", result.Error, StringComparison.Ordinal);
        Assert.Contains("number", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_call_used_as_the_wrong_kind_is_refused_by_the_surrounding_expression()
    {
        var result = Check("custom.discount(total, tier) and paid");

        Assert.NotNull(result.Error);
    }

    [Fact]
    public void An_unclosed_call_says_so()
    {
        Assert.Contains("closing parenthesis", Check("custom.discount(total, tier").Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_call_is_never_collected_as_a_field_dependency()
    {
        var identifiers = ComputedExpr.Identifiers("custom.discount(total, tier)");
        var local = ComputedExpr.LocalIdentifiers("custom.discount(total, tier)");

        Assert.DoesNotContain("custom.discount", identifiers);
        Assert.DoesNotContain("custom.discount", local);
        Assert.Contains("total", identifiers);
        Assert.Contains("tier", identifiers);
    }

    [Fact]
    public void An_undeclared_call_is_still_not_collected_as_a_field()
    {
        // The collectors are context-free and have no registry. A prefix is what keeps them right:
        // were this collected, cycle detection would wait on a field that does not exist.
        Assert.DoesNotContain("custom.rebate", ComputedExpr.Identifiers("custom.rebate(total)"));
    }

    [Fact]
    public void A_reference_field_called_custom_is_still_an_ordinary_hop()
    {
        var result = Check("custom.owner + total");

        Assert.Null(result.Error);
        Assert.Contains("custom.owner", ComputedExpr.Identifiers("custom.owner + total"));
    }

    [Fact]
    public void The_prefix_alone_is_not_a_call()
    {
        Assert.False(ComputedExpr.IsCustomCall("custom."));
        Assert.False(ComputedExpr.IsCustomCall("customer_total"));
        Assert.True(ComputedExpr.IsCustomCall("custom.discount"));
    }

    [Fact]
    public void Parse_and_Validate_agree_about_what_is_declared()
    {
        // The failure this guards: Parse once took no lookup, so an expression validated and then
        // came back null from the emitter, and the field was generated blank.
        Assert.Null(Check("custom.discount(total, tier)").Error);

        var withLookup = ComputedExpr.Parse("custom.discount(total, tier)", Fields, Signatures);
        var without = ComputedExpr.Parse("custom.discount(total, tier)", Fields);

        Assert.NotNull(withLookup);
        Assert.IsType<ComputedExpr.CustomCallNode>(withLookup);
        Assert.Null(without);
    }

    [Fact]
    public void A_parsed_call_carries_the_name_without_the_prefix()
    {
        var node = (ComputedExpr.CustomCallNode)ComputedExpr.Parse(
            "custom.discount(total, tier)", Fields, Signatures)!;

        Assert.Equal("discount", node.Name);
        Assert.Equal(2, node.Args.Count);
        Assert.Equal(ComputedValueKind.Number, node.Kind);
    }

    [Fact]
    public void With_no_lookup_at_all_a_call_is_simply_undeclared()
    {
        var result = ComputedExpr.Validate("custom.discount(total, tier)", Fields);

        Assert.NotNull(result.Error);
        Assert.Contains("not a function this application declares", result.Error, StringComparison.Ordinal);
    }
}

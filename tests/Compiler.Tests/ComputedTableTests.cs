// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.Definition;

namespace Cordango.Compiler.Tests;

/// <summary>
/// The two table forms, and the fold that is the whole of their implementation.
///
/// <para>What these mostly prove is that nothing downstream has to change: a table parses to the same
/// <see cref="ComputedExpr.ConditionalNode"/> tree the nested <c>if</c> chain it replaces parses to.
/// So the evaluator, both emitters and the Node port keep computing the figures they already computed,
/// and the shape assertions here are what pins that.</para>
/// </summary>
public class ComputedTableTests
{
    private static readonly Dictionary<string, ComputedValueKind> Kinds = new(StringComparer.Ordinal)
    {
        ["gross"] = ComputedValueKind.Number,
        ["months"] = ComputedValueKind.Number,
        ["children"] = ComputedValueKind.Number,
        ["state"] = ComputedValueKind.Text,
        ["label"] = ComputedValueKind.Text,
        ["flagged"] = ComputedValueKind.Boolean,
        ["due"] = ComputedValueKind.Date,
    };

    private static readonly HashSet<string> StateCodes =
        new(StringComparer.Ordinal) { "bayern", "sachsen", "berlin" };

    private static ComputedExprValidation Check(string expr) => ComputedExpr.Validate(
        expr,
        key => Kinds.TryGetValue(key, out var kind) ? kind : null,
        key => Kinds.ContainsKey(key) ? null : $"'{key}' is not a field",
        codeError: (field, literal) => field == "state" && !StateCodes.Contains(literal)
            ? $"'{literal}' is not an option of '{field}'"
            : null);

    private static string? Error(string expr) => Check(expr).Error;

    private static ComputedExpr.Node? Tree(string expr) =>
        ComputedExpr.Parse(expr, key => Kinds.TryGetValue(key, out var kind) ? kind : null);

    [Fact]
    public void A_table_folds_into_the_conditionals_it_means()
    {
        var folded = Tree("switch(state, 'bayern', 1, 'sachsen', 2, 3)");

        var first = Assert.IsType<ComputedExpr.ConditionalNode>(folded);
        var firstTest = Assert.IsType<ComputedExpr.BinaryNode>(first.Condition);
        Assert.Equal("==", firstTest.Op);
        Assert.Equal("state", Assert.IsType<ComputedExpr.FieldNode>(firstTest.Left).Key);
        Assert.Equal("bayern", Assert.IsType<ComputedExpr.TextNode>(firstTest.Right).Value);
        Assert.Equal(1m, Assert.IsType<ComputedExpr.NumberNode>(first.Then).Value);

        var second = Assert.IsType<ComputedExpr.ConditionalNode>(first.Else);
        Assert.Equal(2m, Assert.IsType<ComputedExpr.NumberNode>(second.Then).Value);
        Assert.Equal(3m, Assert.IsType<ComputedExpr.NumberNode>(second.Else).Value);
    }

    [Fact]
    public void A_table_parses_to_exactly_what_the_chain_it_replaces_parses_to() =>
        Assert.Equal(
            Tree("if(state == 'bayern', 1, if(state == 'sachsen', 2, 3))"),
            Tree("switch(state, 'bayern', 1, 'sachsen', 2, 3)"));

    [Fact]
    public void A_ladder_parses_to_exactly_what_the_chain_it_replaces_parses_to() =>
        Assert.Equal(
            Tree("if(gross <= 100, 0, if(gross <= 200, 5, 9))"),
            Tree("case(gross <= 100, 0, gross <= 200, 5, 9)"));

    [Fact]
    public void A_table_takes_its_kind_from_the_answers() =>
        Assert.Equal(ComputedValueKind.Text, Check("switch(gross, 1, 'low', 2, 'mid', 'high')").ResultKind);

    [Theory]
    [InlineData("switch(state, 'bayern', 1, 2, 3)")]
    [InlineData("switch(state, 'bayern', 1, 'sachsen', 2)")]
    public void A_switch_pairs_every_key_with_a_value(string expr) =>
        Assert.Contains("an even number of arguments", Error(expr));

    [Fact]
    public void A_switch_with_no_rows_at_all_is_not_a_table() =>
        Assert.Contains("at least four", Error("switch(state, 1)"));

    [Fact]
    public void A_switch_keys_a_field_not_a_working_out() =>
        Assert.Contains("its first argument is a field", Error("switch(gross * 2, 1, 10, 20)"));

    [Fact]
    public void A_switch_does_not_key_a_table_on_a_boolean() =>
        Assert.Contains("not a boolean", Error("switch(flagged, 1, 10, 20)"));

    [Fact]
    public void A_key_must_be_the_kind_the_field_is() =>
        Assert.Contains("its keys are texts", Error("switch(state, 1, 10, 20)"));

    [Fact]
    public void A_key_is_written_out_not_worked_out() =>
        Assert.Contains("written out, not worked out", Error("switch(state, label, 10, 20)"));

    [Fact]
    public void A_repeated_key_can_never_be_reached() =>
        Assert.Contains("repeats the key 'bayern'", Error("switch(state, 'bayern', 1, 'bayern', 2, 3)"));

    [Fact]
    public void A_repeated_number_key_is_the_same_mistake() =>
        Assert.Contains("repeats the key", Error("switch(children, 1, 10, 1.0, 20, 30)"));

    [Fact]
    public void A_key_no_option_offers_is_refused() =>
        Assert.Contains("'bayren' is not an option of 'state'",
            Error("switch(state, 'bayern', 1, 'bayren', 2, 3)"));

    [Fact]
    public void Every_row_of_a_switch_answers_the_same_kind() =>
        Assert.Contains("same kind of thing every way it can go",
            Error("switch(state, 'bayern', 1, 'sachsen', 'two', 3)"));

    [Fact]
    public void The_default_of_a_switch_answers_that_kind_too() =>
        Assert.Contains("same kind of thing every way it can go",
            Error("switch(state, 'bayern', 1, 'sachsen', 2, 'three')"));

    [Fact]
    public void A_switch_over_a_number_field_is_a_table_too() =>
        Assert.Null(Error("switch(children, 0, 100, 1, 200, 300)"));

    [Theory]
    [InlineData("case(gross > 1, 10, 20, 30)")]
    [InlineData("case(gross > 1, 10, gross > 2, 20, gross > 3, 30)")]
    public void A_case_pairs_every_test_with_a_value(string expr) =>
        Assert.Contains("an odd number of arguments", Error(expr));

    [Fact]
    public void A_case_needs_a_test_and_an_answer_and_a_default() =>
        Assert.Contains("at least three", Error("case(gross)"));

    [Fact]
    public void A_case_tests_something_true_or_false() =>
        Assert.Contains("tests something true or false, not a number", Error("case(gross, 10, 20)"));

    [Fact]
    public void Every_row_of_a_case_answers_the_same_kind() =>
        Assert.Contains("same kind of thing every way it can go",
            Error("case(gross > 1, 10, gross > 2, 'two', 30)"));

    [Fact]
    public void A_five_zone_tariff_is_one_expression() =>
        Assert.Null(Error("""
            case(gross <= 12096, 0,
                 gross <= 17488, (gross - 12096) * 0.00276 + 932.30,
                 gross <= 68480, (gross - 17488) * 0.00311 + 1081.91,
                 gross <= 257936, (gross - 68480) * 0.00321 + 2827.98,
                 (gross - 257936) * 0.00323 + 9944.29)
            """));

    [Fact]
    public void A_table_reads_the_field_it_is_keyed_on() =>
        Assert.Equal(["state"], Check("switch(state, 'bayern', 1, 2)").Identifiers);

    [Theory]
    [InlineData("switch(state, 'bayern', 1, 2)")]
    [InlineData("case(gross > 1, 10, 20)")]
    public void A_table_function_is_not_a_field_the_row_depends_on(string expr)
    {
        Assert.DoesNotContain("switch", ComputedExpr.LocalIdentifiers(expr));
        Assert.DoesNotContain("case", ComputedExpr.LocalIdentifiers(expr));
        Assert.DoesNotContain("switch", ComputedExpr.Identifiers(expr));
        Assert.DoesNotContain("case", ComputedExpr.Identifiers(expr));
    }

    [Fact]
    public void A_date_function_is_not_a_field_the_row_depends_on()
    {
        Assert.Equal(["due"], ComputedExpr.LocalIdentifiers("weekday(due)"));
        Assert.Equal(["due"], ComputedExpr.Identifiers("start_of_month(due)"));
    }
}

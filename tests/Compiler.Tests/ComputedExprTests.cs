// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.Definition;

namespace Cordango.Compiler.Tests;

public class ComputedExprTests
{
    private static readonly Dictionary<string, decimal?> Fields = new()
    {
        ["invested"] = 400000m,
        ["current_value"] = 1300000m,
        ["realized"] = 0m,
        ["zero"] = 0m,
        ["available"] = 6m,
        ["minimum"] = 8m,
    };
    private static readonly Dictionary<string, bool?> Booleans = new()
    {
        ["active"] = true,
        ["archived"] = false,
    };
    private static readonly Dictionary<string, DateTimeOffset?> Dates = new()
    {
        ["entry_date"] = new DateTimeOffset(2023, 1, 15, 0, 0, 0, TimeSpan.Zero),
        ["valued_on"] = new DateTimeOffset(2026, 6, 28, 0, 0, 0, TimeSpan.Zero),
        ["unset"] = null,
    };

    private static string? Parse(string expr) => ComputedExpr.Error(
        expr,
        ident => Fields.ContainsKey(ident) ? null : $"'{ident}' is not a field",
        arg => Dates.ContainsKey(arg) ? null : $"'{arg}' is not a date field");

    private static ComputedExprValidation Typed(string expr) => ComputedExpr.Validate(
        expr,
        key => Fields.ContainsKey(key) ? ComputedValueKind.Number
            : Booleans.ContainsKey(key) ? ComputedValueKind.Boolean
            : Dates.ContainsKey(key) ? ComputedValueKind.Date
            : null,
        key => Fields.ContainsKey(key) || Booleans.ContainsKey(key) || Dates.ContainsKey(key)
            ? null : $"'{key}' is not a field",
        key => Dates.ContainsKey(key) ? null : $"'{key}' is not a date field");

    [Theory]
    [InlineData("pow(2, 10)")]
    [InlineData("pow(current_value / invested, 2)")]
    [InlineData("(pow((current_value + realized) / invested, 365 / days_between(entry_date, valued_on)) - 1) * 100")]
    public void Pow_takes_two_sub_expressions(string expr) => Assert.Null(Parse(expr));

    [Theory]
    [InlineData("pow(2)")]
    [InlineData("pow(2, 3, 4)")]
    public void Pow_with_the_wrong_arity_is_rejected(string expr) =>
        Assert.Equal("'pow' takes exactly two arguments", Parse(expr));

    [Fact]
    public void Pow_validates_its_arguments_as_ordinary_identifiers() =>
        Assert.Equal("'nope' is not a field", Parse("pow(nope, 2)"));

    [Fact]
    public void An_unknown_function_still_reports_as_unknown() =>
        Assert.Equal("'sqrt' is not a known function", Parse("sqrt(invested)"));

    [Theory]
    [InlineData("min(invested, current_value)")]
    [InlineData("max(0, invested - current_value)")]
    [InlineData("min(available * minimum, 100 / 4)")]
    public void Min_and_max_take_two_sub_expressions(string expr) => Assert.Null(Parse(expr));

    [Theory]
    [InlineData("min(invested)")]
    [InlineData("max(invested)")]
    [InlineData("min(1, 2, 3)")]
    public void Min_and_max_with_the_wrong_arity_are_rejected(string expr) =>
        Assert.Contains("takes exactly two arguments", Parse(expr));

    [Fact]
    public void Min_validates_its_arguments_as_ordinary_identifiers() =>
        Assert.Equal("'nope' is not a field", Parse("min(nope, 2)"));

    [Fact]
    public void Min_refuses_a_boolean_argument() =>
        Assert.Equal("'min' takes two numbers", Typed("min(available < minimum, 2)").Error);

    [Fact]
    public void Duration_functions_still_take_bare_date_fields_only()
    {
        Assert.Null(Parse("days_between(entry_date, valued_on)"));
        Assert.Equal("'days_between' takes date fields, not '2'", Parse("days_between(2, valued_on)"));
    }

    [Theory]
    [InlineData("available < minimum")]
    [InlineData("available <= minimum and active")]
    [InlineData("not archived or available == 0")]
    public void Comparisons_and_boolean_logic_are_statically_boolean(string expr)
    {
        var result = Typed(expr);
        Assert.Null(result.Error);
        Assert.Equal(ComputedValueKind.Boolean, result.ResultKind);
    }

    [Fact]
    public void Arithmetic_cannot_consume_a_boolean()
    {
        var result = Typed("available + active");
        Assert.Equal("operator '+' requires numbers", result.Error);
    }

    [Fact]
    public void Dates_can_be_compared_but_not_used_as_numbers()
    {
        var comparison = Typed("entry_date < valued_on");
        Assert.Null(comparison.Error);
        Assert.Equal(ComputedValueKind.Boolean, comparison.ResultKind);

        Assert.Equal("operator '+' requires numbers", Typed("entry_date + 1").Error);
        Assert.Equal("operator '<' requires two numbers or two dates", Typed("entry_date < available").Error);
    }
}

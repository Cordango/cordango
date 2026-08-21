// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.Definition;

namespace Cordango.Compiler.Tests;

/// <summary>The computed-field grammar itself. The Gate parses with <c>Error</c> and the runtime
/// evaluates with <c>Evaluate</c>, so anything the first accepts the second must be able to run —
/// these pin both halves together, with the portfolio "annualized return" case that motivated
/// <c>pow</c> as the worked example.</summary>
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

    private static decimal? Eval(string expr) => ComputedExpr.Evaluate(
        expr,
        k => Fields.TryGetValue(k, out var v) ? v : null,
        k => Dates.TryGetValue(k, out var d) ? d : null);

    private static ComputedExprValidation Typed(string expr) => ComputedExpr.Validate(
        expr,
        key => Fields.ContainsKey(key) ? ComputedValueKind.Number
            : Booleans.ContainsKey(key) ? ComputedValueKind.Boolean
            : Dates.ContainsKey(key) ? ComputedValueKind.Date
            : null,
        key => Fields.ContainsKey(key) || Booleans.ContainsKey(key) || Dates.ContainsKey(key)
            ? null : $"'{key}' is not a field",
        key => Dates.ContainsKey(key) ? null : $"'{key}' is not a date field");

    private static bool? EvalBool(string expr) => ComputedExpr.EvaluateValue(
        expr,
        key => Fields.ContainsKey(key) ? ComputedValueKind.Number
            : Booleans.ContainsKey(key) ? ComputedValueKind.Boolean
            : Dates.ContainsKey(key) ? ComputedValueKind.Date
            : null,
        key => Fields.GetValueOrDefault(key),
        key => Booleans.GetValueOrDefault(key),
        key => Dates.GetValueOrDefault(key))?.Boolean;

    // ---- parsing -------------------------------------------------------------------------------

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

    /// <summary>The clamp is numeric on both sides. A comparison passed to it would be the author
    /// reaching for the conditional this language does not have.</summary>
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

    // ---- evaluation ----------------------------------------------------------------------------

    [Fact]
    public void Pow_evaluates() => Assert.Equal(1024m, Eval("pow(2, 10)"));

    [Fact]
    public void Annualized_return_is_expressible()
    {
        // €400k into FinPilot on 2023-01-15, marked at €1.30M on 2026-06-28: 3.25x over ~3.45 years.
        var cagr = Eval("(pow((current_value + realized) / invested, 365 / days_between(entry_date, valued_on)) - 1) * 100");
        Assert.NotNull(cagr);
        Assert.InRange(cagr!.Value, 40m, 41m);
    }

    [Fact]
    public void A_total_loss_annualizes_to_minus_one_hundred_percent()
    {
        // A written-off holding is marked at 0 — 0^x is 0, so the rate is exactly -100%, not an error.
        var cagr = Eval("(pow(zero / invested, 365 / days_between(entry_date, valued_on)) - 1) * 100");
        Assert.Equal(-100m, cagr);
    }

    [Fact]
    public void A_null_operand_yields_null_rather_than_a_wrong_number()
    {
        // An unset date makes days_between null, so the exponent is null and the whole rate is empty.
        Assert.Null(Eval("pow(current_value / invested, 365 / days_between(entry_date, unset))"));
        // Same for a division by zero inside the exponent — a same-day mark can't be annualized.
        Assert.Null(Eval("pow(current_value / invested, 365 / zero)"));
    }

    [Fact]
    public void A_negative_base_under_a_fractional_exponent_is_null_not_NaN() =>
        Assert.Null(Eval("pow(0 - 8, 1 / 2)"));

    [Fact]
    public void An_overflowing_power_is_null() => Assert.Null(Eval("pow(10, 1000)"));

    [Fact]
    public void Inventory_warning_comparison_evaluates() =>
        Assert.True(EvalBool("available < minimum and not archived"));

    [Fact]
    public void Arrival_date_comparison_evaluates_and_an_unset_date_is_unknown()
    {
        Assert.True(EvalBool("entry_date <= valued_on"));
        Assert.Null(EvalBool("unset <= valued_on"));
    }

    /// <summary>
    /// The case min/max exist for: a usage charge that stops at a ceiling.
    ///
    /// <para>Cordango bills active-user x app combinations and caps the total per plan. Under the old
    /// grammar the capped figure could not be written at all — there is no conditional, and a boolean
    /// is not a number, so the arithmetic dodge is refused too (see
    /// <see cref="Arithmetic_cannot_consume_a_boolean"/>). The author's only remaining move was to
    /// type the clamped number by hand every month.</para>
    /// </summary>
    [Fact]
    public void A_charge_can_be_capped()
    {
        // 6 units at 8 apiece is 48; the plan's ceiling is 40.
        Assert.Equal(40m, Eval("min(available * minimum, 40)"));
        // Under the ceiling the charge passes through untouched.
        Assert.Equal(48m, Eval("min(available * minimum, 60)"));
    }

    [Fact]
    public void A_balance_can_be_floored_at_zero()
    {
        Assert.Equal(0m, Eval("max(0, available - minimum)"));
        Assert.Equal(2m, Eval("max(0, minimum - available)"));
    }

    /// <summary>A bound that could not be worked out is not the same as no bound. Returning the other
    /// side would silently un-cap the charge, which is the one failure nobody would notice.</summary>
    [Fact]
    public void A_bound_that_cannot_be_worked_out_is_null_not_the_other_side()
    {
        Assert.Null(Eval("min(invested, 1 / zero)"));
        Assert.Null(Eval("max(1 / zero, invested)"));
    }

    [Fact]
    public void Min_and_max_are_exact_where_pow_would_not_be()
    {
        // Decimal all the way through: pow routes via double, so a money figure clamped with the
        // (a + b - |a - b|) / 2 trick would come back with float noise. This does not.
        Assert.Equal(53.90m, Eval("min(53.90, 125.00)"));
    }
}

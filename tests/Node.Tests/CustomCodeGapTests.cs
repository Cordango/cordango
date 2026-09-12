// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.SourceGen.Common;
using Cordango.SourceGen.Node.Emit;

namespace Cordango.Node.Tests;

/// <summary>
/// Custom code belongs to the target that owns its language, and this target does not own C#.
///
/// <para>The expression travels to this runtime as TEXT and is parsed when the application starts,
/// so a call this generator lets through is not a build failure — it is an application that dies on
/// boot. These pin the refusal, and pin that it names the right cause.</para>
/// </summary>
public class CustomCodeGapTests
{
    private static FieldModel Computed(string expression) =>
        new(new JsonObject
        {
            ["key"] = "total",
            ["type"] = "decimal",
            ["computed"] = new JsonObject { ["expr"] = expression },
        }, "invoice");

    [Theory]
    [InlineData("custom.discount(amount, tier)")]
    [InlineData("amount - custom.discount(amount, tier)")]
    [InlineData("custom.fee()")]
    public void A_call_into_custom_code_is_not_emitted(string expression)
    {
        Assert.False(ComputedEmitter.IsLocalExpression(Computed(expression)));
    }

    [Fact]
    public void The_refusal_names_custom_code_rather_than_a_reference()
    {
        var why = ComputedEmitter.WhyNotEmitted(Computed("custom.discount(amount, tier)"));

        Assert.NotNull(why);
        Assert.Contains("custom code", why, StringComparison.Ordinal);
        Assert.DoesNotContain("across a reference", why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reference_field_named_custom_is_still_an_ordinary_hop()
    {
        var why = ComputedEmitter.WhyNotEmitted(Computed("custom.owner_rate * hours"));

        Assert.NotNull(why);
        Assert.Contains("across a reference", why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_field_whose_name_merely_ends_in_custom_is_untouched()
    {
        var why = ComputedEmitter.WhyNotEmitted(Computed("my_custom.rate * hours"));

        Assert.NotNull(why);
        Assert.Contains("across a reference", why, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ordinary_local_expression_still_emits()
    {
        Assert.True(ComputedEmitter.IsLocalExpression(Computed("amount * 1.2")));
    }
}

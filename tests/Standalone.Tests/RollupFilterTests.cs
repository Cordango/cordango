// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Definition;
using Cordango.SourceGen;
using Cordango.SourceGen.DotNet.Emit;
using Cordango.SourceGen.Common;

namespace Cordango.Standalone.Tests;

public class RollupFilterTests
{
    [Fact]
    public void An_in_filter_becomes_a_disjunction()
    {
        var query = Query(Filter("status", "in", new JsonArray("approved", "planned", "active")));

        Assert.Contains(
            ".Where(x => x.Status == \"approved\" || x.Status == \"planned\" || x.Status == \"active\")",
            query, StringComparison.Ordinal);
    }

    [Fact]
    public void A_notIn_filter_negates_the_whole_disjunction()
    {
        var query = Query(Filter("status", "notIn", new JsonArray("draft", "void")));

        Assert.Contains(
            ".Where(x => !(x.Status == \"draft\" || x.Status == \"void\"))", query, StringComparison.Ordinal);
    }

    [Fact]
    public void In_nothing_matches_nothing_and_notIn_nothing_matches_everything()
    {
        Assert.Contains(".Where(x => false)", Query(Filter("status", "in", new JsonArray())),
            StringComparison.Ordinal);

        Assert.Contains(".Where(x => true)", Query(Filter("status", "notIn", new JsonArray())),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("gt", ">")]
    [InlineData("gte", ">=")]
    [InlineData("lt", "<")]
    [InlineData("lte", "<=")]
    public void An_ordered_comparison_is_written_against_a_typed_literal(string op, string symbol)
    {
        Assert.Contains($".Where(x => x.Amount {symbol} 100m)", Query(Filter("amount", op, 100)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_number_is_written_as_a_number_rather_than_as_null()
    {
        Assert.Contains(".Where(x => x.Amount == 100m)", Query(Filter("amount", "eq", 100)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void An_integer_column_takes_an_integer_literal()
    {
        Assert.Contains(".Where(x => x.Quantity == 3L)", Query(Filter("quantity", "eq", 3)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_date_is_written_as_a_constructed_date()
    {
        Assert.Contains(".Where(x => x.Due >= new DateOnly(2026, 9, 18))",
            Query(Filter("due", "gte", "2026-09-18")), StringComparison.Ordinal);
    }

    [Fact]
    public void A_boolean_column_takes_a_boolean_literal()
    {
        Assert.Contains(".Where(x => x.Billable == true)", Query(Filter("billable", "eq", true)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Between_is_one_leaf_with_both_ends_included()
    {
        var query = Query(Filter("amount", "between", new JsonArray(10, 20)));

        Assert.Contains(".Where(x => x.Amount >= 10m && x.Amount <= 20m)", query, StringComparison.Ordinal);
    }

    [Fact]
    public void Contains_guards_the_null_it_would_otherwise_call_into()
    {
        Assert.Contains(".Where(x => x.Status != null && x.Status.Contains(\"pend\"))",
            Query(Filter("status", "contains", "pend")), StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_text_means_null_or_blank()
    {
        Assert.Contains(".Where(x => x.Status == null || x.Status == \"\")",
            Query(Filter("status", "isEmpty", null)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_required_value_column_is_never_empty()
    {
        Assert.Contains(".Where(x => false)", Query(Filter("quantity", "isEmpty", null)),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("gt")]
    [InlineData("lt")]
    [InlineData("between")]
    public void Text_has_no_order_to_compare(string op)
    {
        object value = op == "between" ? new JsonArray("a", "b") : "a";

        Assert.Null(Rollup(Filter("status", op, value)));
    }

    [Fact]
    public void A_multiselect_is_refused_rather_than_compared_against_a_string()
    {
        Assert.Null(Rollup(Filter("tags", "eq", "urgent")));
    }

    [Fact]
    public void A_scope_token_is_refused_because_a_recompute_has_no_screen()
    {
        Assert.Null(Rollup(Filter("status", "eq", "{{state.chosen}}")));
        Assert.Null(Rollup(Filter("due", "lte", "{{today}}")));
    }

    [Fact]
    public void A_hop_is_refused_because_it_is_a_join()
    {
        var leaf = new JsonObject
        {
            ["path"] = "project.status",
            ["operator"] = "eq",
            ["value"] = "active",
        };

        Assert.Null(Rollup(leaf));
    }

    [Fact]
    public void One_unwritable_leaf_takes_the_whole_rollup()
    {
        Assert.Null(Rollup(
            Filter("status", "eq", "approved"),
            Filter("status", "overlaps", "a")));
    }

    [Fact]
    public void The_emitter_writes_every_filter_operator_the_schema_allows()
    {
        string[] refused = ["overlaps"];

        var options = (JsonArray)Schemas.AppDefinitionSchemaNode()["$defs"]!["filter"]!["properties"]!
            ["operator"]!["enum"]!;

        foreach (var option in options.OfType<JsonValue>())
        {
            var op = option.GetValue<string>();
            if (refused.Contains(op, StringComparer.Ordinal)) continue;

            Assert.True(RollupEmitter.Operators.Contains(op),
                $"the schema lets a rollup filter on '{op}' and this target does not write it, so "
                + "`cordango check` accepts the field and the column stays empty at CORD2305.");
        }
    }

    private static JsonObject Filter(string field, string @operator, object? value)
    {
        var leaf = new JsonObject { ["field"] = field, ["operator"] = @operator };

        leaf["value"] = value switch
        {
            null => null,
            JsonNode node => node,
            string text => JsonValue.Create(text),
            int number => JsonValue.Create(number),
            bool flag => JsonValue.Create(flag),
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };

        return leaf;
    }

    private static string Query(params JsonObject[] filters) =>
        Rollup(filters) ?? throw new InvalidOperationException("The rollup was refused.");

    private static string? Rollup(params JsonObject[] filters)
    {
        var (app, parent) = Application();

        var rollup = new JsonObject
        {
            ["entity"] = "line",
            ["via"] = "invoice",
            ["op"] = "sum",
            ["field"] = "amount",
            ["filters"] = new JsonArray([.. filters.Select(f => (JsonNode)f)]),
        };

        var field = new FieldModel(new JsonObject
        {
            ["key"] = "total",
            ["label"] = "Total",
            ["type"] = "decimal",
            ["computed"] = new JsonObject { ["rollup"] = rollup },
        }, "invoice");

        return RollupEmitter.Query(app, parent, field);
    }

    private static (AppModel App, EntityModel Parent) Application()
    {
        var parent = new JsonObject
        {
            ["key"] = "invoice",
            ["label"] = "Invoice",
            ["fields"] = new JsonArray(
                new JsonObject { ["key"] = "total", ["label"] = "Total", ["type"] = "decimal" }),
        };

        var child = new JsonObject
        {
            ["key"] = "line",
            ["label"] = "Line",
            ["fields"] = new JsonArray(
                new JsonObject
                {
                    ["key"] = "invoice",
                    ["label"] = "Invoice",
                    ["type"] = "reference",
                    ["target"] = "invoice",
                },
                new JsonObject { ["key"] = "amount", ["label"] = "Amount", ["type"] = "decimal" },
                new JsonObject
                {
                    ["key"] = "quantity",
                    ["label"] = "Quantity",
                    ["type"] = "integer",
                    ["required"] = true,
                },
                new JsonObject { ["key"] = "due", ["label"] = "Due", ["type"] = "date" },
                new JsonObject { ["key"] = "billable", ["label"] = "Billable", ["type"] = "boolean" },
                new JsonObject { ["key"] = "tags", ["label"] = "Tags", ["type"] = "multiselect" },
                new JsonObject { ["key"] = "status", ["label"] = "Status", ["type"] = "text" }),
        };

        var manifest = new JsonObject
        {
            ["key"] = "billing",
            ["name"] = "Billing",
            ["entities"] = new JsonArray(parent.DeepClone(), child.DeepClone()),
        };

        var app = AppModel.From(new CompiledAppArtifact(
            manifest, manifest, "unhashed", new CompilerInfo("test", "1")));

        return (app, new EntityModel(parent, "Billing"));
    }
}

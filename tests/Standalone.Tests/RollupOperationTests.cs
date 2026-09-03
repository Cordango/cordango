// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Definition;
using Cordango.SourceGen;
using Cordango.SourceGen.DotNetVue.Emit;
using Cordango.SourceGen.Common;

namespace Cordango.Standalone.Tests;

public class RollupOperationTests
{
    [Theory]
    [InlineData("sum", "SumAsync")]
    [InlineData("avg", "AverageAsync")]
    [InlineData("min", "MinAsync")]
    [InlineData("max", "MaxAsync")]
    public void Every_aggregating_operation_is_written(string op, string method)
    {
        var (app, parent) = Application();

        var query = RollupEmitter.Query(app, parent, Rollup(op, "amount"));

        Assert.NotNull(query);
        Assert.Contains($".{method}(x => (decimal?)x.Amount, ct)", query, StringComparison.Ordinal);
        Assert.Contains(".Where(x => x.Invoice == r.Id)", query, StringComparison.Ordinal);
    }

    [Fact]
    public void Count_needs_no_field()
    {
        var (app, parent) = Application();

        var query = RollupEmitter.Query(app, parent, Rollup("count", field: null));

        Assert.NotNull(query);
        Assert.Contains(".CountAsync(ct)", query, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sum")]
    [InlineData("avg")]
    [InlineData("min")]
    [InlineData("max")]
    public void An_aggregating_operation_without_a_field_is_refused(string op)
    {
        var (app, parent) = Application();

        Assert.Null(RollupEmitter.Query(app, parent, Rollup(op, field: null)));
    }

    [Fact]
    public void An_operation_outside_the_language_is_refused()
    {
        var (app, parent) = Application();

        Assert.Null(RollupEmitter.Query(app, parent, Rollup("median", "amount")));
    }

    [Fact]
    public void The_emitter_writes_every_operation_the_schema_allows()
    {
        var options = (JsonArray)Schemas.AppDefinitionSchemaNode()["$defs"]!["field"]!["properties"]!
            ["computed"]!["properties"]!["rollup"]!["properties"]!["op"]!["enum"]!;

        foreach (var option in options.OfType<JsonValue>())
        {
            var op = option.GetValue<string>();
            Assert.True(RollupEmitter.Ops.Contains(op),
                $"the schema lets a definition write rollup op '{op}' and this target does not emit "
                + "it, so `cordango check` accepts the field and the column stays empty at CORD2305.");
        }
    }

    private static FieldModel Rollup(string op, string? field)
    {
        var rollup = new JsonObject
        {
            ["entity"] = "line",
            ["via"] = "invoice",
            ["op"] = op,
        };

        if (field is not null) rollup["field"] = field;

        return new FieldModel(new JsonObject
        {
            ["key"] = "total",
            ["label"] = "Total",
            ["type"] = "decimal",
            ["computed"] = new JsonObject { ["rollup"] = rollup },
        }, "invoice");
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
                new JsonObject { ["key"] = "amount", ["label"] = "Amount", ["type"] = "decimal" }),
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

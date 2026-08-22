// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Cord;
using Xunit.Abstractions;

namespace Cordango.Compiler.Tests;

public class CordSplitTests(ITestOutputHelper output)
{
    private static CordApp Money() => CordImport.Import(JsonNode.Parse(
        """
        {"key":"fin","name":"Finance","version":"1.0.0","entities":[
          {"key":"expense","label":"Expense","fields":[
            {"key":"amount","label":"Amount","type":"number"},
            {"key":"category","label":"Category","type":"select","options":[
              {"value":"travel","label":"Travel"},{"value":"kit","label":"Kit"}]},
            {"key":"month","label":"Month","type":"date"}]}]}
        """)!);

    private static (CordApp App, IReadOnlyList<CordError> Errors) Apply(CordApp draft, string json)
    {
        var prepared = CordTransaction.Prepare(draft, JsonNode.Parse(json)!["ops"],
            CordOps.UiOpNames);
        return (prepared.Next, prepared.Errors);
    }

    private static JsonArray Blocks(CordApp app) =>
        (JsonArray)((JsonObject)CordLower.Lower(app))["pages"]![0]!["blocks"]!;

    [Fact]
    public void Two_charts_read_together_become_one_columns_block_with_no_weights()
    {
        var (app, errors) = Apply(Money(), """
        {"ops":[{"op":"upsert_screen","screen":{"key":"spend","label":"Spend","sections":[
          {"kind":"split","sections":[
            {"kind":"chart","of":"expense","label":"By category","groupBy":"category",
             "value":{"op":"sum","field":"amount"}},
            {"kind":"chart","of":"expense","label":"By month","groupBy":"month",
             "value":{"op":"sum","field":"amount"}}]}]}}]}
        """);

        Assert.Empty(errors);
        var block = (JsonObject)Assert.Single(Blocks(app))!;
        output.WriteLine(block.ToJsonString());

        Assert.Equal("columns", (string)block["kind"]!);
        Assert.Null(block["weights"]);

        var columns = block["columns"]!.AsArray();
        Assert.Equal(2, columns.Count);
        foreach (var (column, label) in columns.Zip(new[] { "By category", "By month" }))
        {
            var card = (JsonObject)Assert.Single(column!.AsArray())!;
            Assert.Equal("card", (string)card["kind"]!);
            Assert.Equal(label, (string)card["label"]!);
            Assert.Equal("chart", (string)Assert.Single(card["blocks"]!.AsArray())!["kind"]!);
        }
    }

    [Fact]
    public void A_chart_beside_supporting_content_carries_its_ratio()
    {
        var (app, errors) = Apply(Money(), """
        {"ops":[{"op":"upsert_screen","screen":{"key":"spend","label":"Spend","sections":[
          {"kind":"split","ratio":[2,1],"sections":[
            {"kind":"chart","of":"expense","label":"Spend over time","visual":"line",
             "groupBy":"month","value":{"op":"sum","field":"amount"}},
            {"kind":"list","of":"expense","label":"Largest","view":"table",
             "sort":[{"field":"amount","direction":"desc"}]}]}]}}]}
        """);

        Assert.Empty(errors);
        var block = (JsonObject)Assert.Single(Blocks(app))!;
        output.WriteLine(block.ToJsonString());

        Assert.Equal("columns", (string)block["kind"]!);
        Assert.Equal([2d, 1d], block["weights"]!.AsArray().Select(w => (double)w!));

        var chart = (JsonObject)Assert.Single(
            ((JsonObject)block["columns"]![0]![0]!)["blocks"]!.AsArray())!;
        Assert.Equal("line", (string)chart["chartType"]!);

        var view = (JsonObject)block["columns"]![1]![0]!;
        Assert.Equal("view", (string)view["kind"]!);
        var views = ((JsonObject)CordLower.Lower(app))["views"]!.AsArray();
        Assert.Equal((string)view["view"]!, (string)Assert.Single(views)!["key"]!);
    }

    [Fact]
    public void An_explicitly_equal_ratio_still_emits_no_weights()
    {
        var (app, errors) = Apply(Money(), """
        {"ops":[{"op":"upsert_screen","screen":{"key":"s","label":"S","sections":[
          {"kind":"split","ratio":[1,1],"sections":[
            {"kind":"metric","of":"expense","label":"Total","value":{"op":"sum","field":"amount"}},
            {"kind":"metric","of":"expense","label":"Count","value":{"op":"count"}}]}]}}]}
        """);

        Assert.Empty(errors);
        var block = (JsonObject)Assert.Single(Blocks(app))!;
        Assert.Equal("columns", (string)block["kind"]!);
        Assert.Null(block["weights"]);
    }

    [Fact]
    public void Metrics_still_group_and_a_split_does_not_reach_across_them()
    {
        var (app, errors) = Apply(Money(), """
        {"ops":[{"op":"upsert_screen","screen":{"key":"s","label":"S","sections":[
          {"kind":"metric","of":"expense","label":"Total","value":{"op":"sum","field":"amount"}},
          {"kind":"metric","of":"expense","label":"Count","value":{"op":"count"}},
          {"kind":"split","sections":[
            {"kind":"chart","of":"expense","label":"A","groupBy":"category","value":{"op":"count"}},
            {"kind":"chart","of":"expense","label":"B","groupBy":"month","value":{"op":"count"}}]},
          {"kind":"metric","of":"expense","label":"Average","value":{"op":"avg","field":"amount"}},
          {"kind":"list","of":"expense","label":"Everything","view":"table"}]}}]}
        """);

        Assert.Empty(errors);
        var blocks = Blocks(app);
        output.WriteLine(string.Join(" ", blocks.Select(b => (string)b!["kind"]!)));

        Assert.Equal(["row", "columns", "row", "view"], blocks.Select(b => (string)b!["kind"]!));
        Assert.Equal(2, blocks[0]!["blocks"]!.AsArray().Count);
        Assert.Equal(1, blocks[2]!["blocks"]!.AsArray().Count);
    }

    [Theory]
    [InlineData("", true, "donut")]
    [InlineData("", false, "bar")]
    [InlineData("auto", true, "donut")]
    [InlineData("auto", false, "bar")]
    [InlineData("line", true, "line")]
    [InlineData("area", false, "area")]
    [InlineData("bar", true, "bar")]
    [InlineData("donut", false, "donut")]
    public void A_chart_is_drawn_the_way_the_author_said_or_the_way_the_measure_implies(
        string visual, bool grouped, string want)
    {
        var section = new JsonObject
        {
            ["kind"] = "chart",
            ["of"] = "expense",
            ["label"] = "Spend",
            ["value"] = new JsonObject { ["op"] = "sum", ["field"] = "amount" },
        };
        if (grouped) section["groupBy"] = "category";
        if (visual.Length > 0) section["visual"] = visual;

        var ops = new JsonObject
        {
            ["ops"] = new JsonArray(new JsonObject
            {
                ["op"] = "upsert_screen",
                ["screen"] = new JsonObject
                {
                    ["key"] = "s",
                    ["label"] = "S",
                    ["sections"] = new JsonArray(section),
                },
            }),
        };

        var (app, errors) = Apply(Money(), ops.ToJsonString());
        Assert.Empty(errors);

        var card = (JsonObject)Assert.Single(Blocks(app))!;
        Assert.Equal(want, (string)Assert.Single(card["blocks"]!.AsArray())!["chartType"]!);
    }

    [Fact]
    public void A_split_inside_a_split_is_refused()
    {
        var (_, errors) = Apply(Money(), """
        {"ops":[{"op":"upsert_screen","screen":{"key":"s","label":"S","sections":[
          {"kind":"split","sections":[
            {"kind":"metric","of":"expense","label":"Total","value":{"op":"count"}},
            {"kind":"split","sections":[
              {"kind":"metric","of":"expense","label":"A","value":{"op":"count"}},
              {"kind":"metric","of":"expense","label":"B","value":{"op":"count"}}]}]}]}}]}
        """);

        var error = Assert.Single(errors);
        output.WriteLine(error.ToString());
        Assert.Equal(CordErrorCode.NestedSplit, error.Code);
        Assert.Contains("grid", error.Message);
    }

    [Fact]
    public void Sections_on_a_section_that_is_not_a_split_are_refused()
    {
        var (_, errors) = Apply(Money(), """
        {"ops":[{"op":"upsert_screen","screen":{"key":"s","label":"S","sections":[
          {"kind":"chart","of":"expense","label":"Spend","value":{"op":"count"},
           "sections":[
             {"kind":"metric","of":"expense","label":"A","value":{"op":"count"}},
             {"kind":"metric","of":"expense","label":"B","value":{"op":"count"}}]}]}}]}
        """);

        var error = Assert.Single(errors);
        output.WriteLine(error.ToString());
        Assert.Equal(CordErrorCode.NestedSplit, error.Code);
    }

    [Theory]
    [InlineData("one side", """
        [{"kind":"split","sections":[{"kind":"metric","of":"expense","value":{"op":"count"}}]}]
        """)]
    [InlineData("three sides", """
        [{"kind":"split","sections":[
          {"kind":"metric","of":"expense","value":{"op":"count"}},
          {"kind":"metric","of":"expense","value":{"op":"count"}},
          {"kind":"metric","of":"expense","value":{"op":"count"}}]}]
        """)]
    [InlineData("a ratio with three numbers", """
        [{"kind":"split","ratio":[1,2,3],"sections":[
          {"kind":"metric","of":"expense","value":{"op":"count"}},
          {"kind":"metric","of":"expense","value":{"op":"count"}}]}]
        """)]
    [InlineData("a zero-width column", """
        [{"kind":"split","ratio":[0,1],"sections":[
          {"kind":"metric","of":"expense","value":{"op":"count"}},
          {"kind":"metric","of":"expense","value":{"op":"count"}}]}]
        """)]
    [InlineData("a pixel width", """
        [{"kind":"split","ratio":["320px",1],"sections":[
          {"kind":"metric","of":"expense","value":{"op":"count"}},
          {"kind":"metric","of":"expense","value":{"op":"count"}}]}]
        """)]
    [InlineData("a raw block", """
        [{"kind":"orgchart","of":"expense"}]
        """)]
    public void The_shapes_a_split_may_not_take(string what, string sections)
    {
        var (_, errors) = Apply(Money(),
            $$$"""{"ops":[{"op":"upsert_screen","screen":{"key":"s","label":"S","sections":{{{sections}}}}}]}""");

        var error = Assert.Single(errors);
        output.WriteLine($"{what}: {error}");
        Assert.Equal(CordErrorCode.MalformedOperation, error.Code);
    }
}

// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Cord;
using Xunit.Abstractions;

namespace Cordango.Compiler.Tests;

/// <summary>
/// <b>The one arrangement an author may state, and the line it draws.</b>
///
/// <para>Cord's screens deliberately have no layout vocabulary: sections say WHAT belongs on a screen
/// and <see cref="CordLowerScreens"/> decides that four metrics become a row of cards. That is the right
/// default because the arrangement is DERIVABLE — nothing about four metrics wants anything else.</para>
///
/// <para>But two charts side by side is not derivable. It is a claim that they are read together, and no
/// property of either chart implies it. That is the boundary this file tests from both sides: author
/// intent gets a word, renderer mechanics does not.</para>
///
/// <para><b>Two-up, and only two-up, because the corpus says so.</b> All 21 <c>columns</c> blocks across
/// the 15 reference apps hold exactly two columns; not one holds three. 19 of the 21 are equal width and
/// the two that differ are <c>[2,1]</c> and <c>[3,2]</c>. A general grid would be vocabulary bought on
/// speculation, and the ceiling on the UI schema is what makes speculation expensive.</para>
/// </summary>
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

    /// <summary>
    /// Two charts of equal weight: a <c>columns</c> block, and NO weights.
    ///
    /// <para>An absent ratio emits nothing rather than <c>[1,1]</c>. Same layout either way, and the
    /// difference is whether the stored document carries a decision the author never made — 19 of the
    /// corpus's 21 two-column blocks carry no weights, which is the shape being matched.</para>
    /// </summary>
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
        // Each side lowered by the ordinary section rules — a chart in a card, exactly as it would be
        // standing on its own. A split arranges; it does not change what a section IS.
        foreach (var (column, label) in columns.Zip(new[] { "By category", "By month" }))
        {
            var card = (JsonObject)Assert.Single(column!.AsArray())!;
            Assert.Equal("card", (string)card["kind"]!);
            Assert.Equal(label, (string)card["label"]!);
            Assert.Equal("chart", (string)Assert.Single(card["blocks"]!.AsArray())!["kind"]!);
        }
    }

    /// <summary>A chart beside something supporting it: the ratio is carried through as weights. Both of
    /// the corpus's asymmetric two-column blocks are this shape.</summary>
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

        // The wide side is the chart the author asked to be a LINE — see below for why that word exists.
        var chart = (JsonObject)Assert.Single(
            ((JsonObject)block["columns"]![0]![0]!)["blocks"]!.AsArray())!;
        Assert.Equal("line", (string)chart["chartType"]!);

        // The narrow side is a list, and it produced a real view rather than being flattened into one.
        var view = (JsonObject)block["columns"]![1]![0]!;
        Assert.Equal("view", (string)view["kind"]!);
        var views = ((JsonObject)CordLower.Lower(app))["views"]!.AsArray();
        Assert.Equal((string)view["view"]!, (string)Assert.Single(views)!["key"]!);
    }

    /// <summary>An equal ratio said out loud is the same as not saying it. Stated so nobody later
    /// "fixes" the lowerer into emitting <c>[1,1]</c> and changes every stored document.</summary>
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

    /// <summary>
    /// <b>The boundary, in one test.</b> Ordinary sections stack full-width; consecutive metrics still
    /// group into a row; a split interrupts neither.
    ///
    /// <para>Metrics before the split group together, the split stands on its own, and metrics after it
    /// group separately — the grouping never reaches ACROSS the thing the author put in between, because
    /// the order sections were written in is itself a statement.</para>
    /// </summary>
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
        // Two metrics in the first row, one in the second — not three in one.
        Assert.Equal(2, blocks[0]!["blocks"]!.AsArray().Count);
        Assert.Equal(1, blocks[2]!["blocks"]!.AsArray().Count);
    }

    /// <summary>
    /// <b>auto is the default and keeps inferring.</b> The inference was right nearly always and is not
    /// replaced by the new word — <c>visual</c> only speaks when the author does.
    /// </summary>
    [Theory]
    [InlineData("", true, "donut")]        // grouped, nothing said -> the existing inference
    [InlineData("", false, "bar")]         // ungrouped, nothing said -> the existing inference
    [InlineData("auto", true, "donut")]    // said explicitly, and it means the same as silence
    [InlineData("auto", false, "bar")]
    [InlineData("line", true, "line")]     // the case inference cannot reach: a breakdown over TIME
    [InlineData("area", false, "area")]
    [InlineData("bar", true, "bar")]       // overriding the inference in the other direction
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

    // ---- invalid shapes ---------------------------------------------------------------------------

    /// <summary>
    /// <b>A split inside a split is a grid, and is refused rather than flattened.</b>
    ///
    /// <para>Flattening would be Cord deciding something the author did not: three things in a row and
    /// two things beside each other are different screens, and picking one silently is exactly the class
    /// of guess this layer exists to remove.</para>
    /// </summary>
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

    /// <summary>Sections on something that is not a split. The schema cannot say this without a second
    /// copy of the whole section shape, which the UI ceiling does not have room for — so it is named
    /// here rather than being unrepresentable, and the reason is written down.</summary>
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

    /// <summary>
    /// Everything the SCHEMA refuses, so the split cannot become a grid by growing.
    ///
    /// <para>Count and ratio length are pinned by <c>minItems</c>/<c>maxItems</c> rather than by a check,
    /// which makes a three-way split unrepresentable instead of merely rejected. That is the stronger
    /// form and it is what the corpus licenses: 21 of 21 two-column blocks.</para>
    /// </summary>
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

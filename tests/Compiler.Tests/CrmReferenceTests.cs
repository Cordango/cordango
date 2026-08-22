// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Definition;

namespace Cordango.Compiler.Tests;

public class CrmReferenceTests
{
    private static JsonNode Reference() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "sales-crm.appdef.json")))!;

    [Fact]
    public void Sales_crm_reference_passes_the_gate_un_salvaged()
    {
        var errors = Gate.Validate(Reference());
        Assert.Empty(errors);
    }

    [Fact]
    public void Stage_is_orthogonal_to_the_won_lost_process()
    {
        var doc = (JsonObject)Reference();
        var deal = doc["entities"]!.AsArray().First(e => e!["key"]!.GetValue<string>() == "deal")!.AsObject();
        var fields = deal["fields"]!.AsArray().Cast<JsonObject>().ToDictionary(f => f!["key"]!.GetValue<string>());

        Assert.Equal("select", fields["stage"]!["type"]!.GetValue<string>());
        Assert.Null(fields["stage"]!["role"]);

        Assert.Equal("status", fields["status"]!["role"]!.GetValue<string>());
        var flow = doc["processes"]!.AsArray().First(p => p!["entity"]!.GetValue<string>() == "deal")!.AsObject();
        Assert.Equal("status", flow["stateField"]!.GetValue<string>());
        var transitionKeys = flow["transitions"]!.AsArray().Select(t => t!["key"]!.GetValue<string>()).ToList();
        Assert.Equal(new[] { "win", "lose", "reopen" }, transitionKeys);
    }

    [Fact]
    public void Deals_board_shows_only_open_deals_grouped_by_stage()
    {
        var doc = (JsonObject)Reference();
        var page = doc["pages"]!.AsArray().First(p => p!["key"]!.GetValue<string>() == "deals")!.AsObject();
        var boards = new List<JsonObject>();
        Collect(page["blocks"], "board", boards);
        var board = Assert.Single(boards);
        Assert.Equal("stage", board["groupField"]!.GetValue<string>());
        Assert.Contains(board["source"]!["filters"]!.AsArray(),
            f => f!["field"]!.GetValue<string>() == "status" && f["value"]!.GetValue<string>() == "open");
    }

    [Fact]
    public void Deals_board_sums_value_per_column_for_the_pipeline_funnel()
    {
        var doc = (JsonObject)Reference();
        var page = doc["pages"]!.AsArray().First(p => p!["key"]!.GetValue<string>() == "deals")!.AsObject();
        var boards = new List<JsonObject>();
        Collect(page["blocks"], "board", boards);
        var board = Assert.Single(boards);
        Assert.Equal("value", board["sumField"]!.GetValue<string>());
    }

    [Fact]
    public void Deals_board_and_list_share_one_search_and_owner_facet()
    {
        var doc = (JsonObject)Reference();
        var page = doc["pages"]!.AsArray().First(p => p!["key"]!.GetValue<string>() == "deals")!.AsObject();

        var bars = new List<JsonObject>();
        Collect(page["blocks"], "filterbar", bars);
        var bar = Assert.Single(bars);
        Assert.Equal("q", bar["search"]!["state"]!.GetValue<string>());
        Assert.Contains(bar["facets"]!.AsArray(), f => f!["field"]!.GetValue<string>() == "owner");

        foreach (var kind in new[] { "board", "table" })
        {
            var surfaces = new List<JsonObject>();
            Collect(page["blocks"], kind, surfaces);
            var surface = Assert.Single(surfaces);
            Assert.Equal("q", surface["search"]!["state"]!.GetValue<string>());
            Assert.Contains(surface["source"]!["filters"]!.AsArray(),
                f => f!["field"]!.GetValue<string>() == "owner"
                     && f["optional"]?.GetValue<bool>() == true
                     && f["value"]!.GetValue<string>() == "{{state.owner}}");
        }
    }

    [Fact]
    public void Lead_conversion_creates_the_deal_from_the_lead_record()
    {
        var doc = (JsonObject)Reference();
        var convert = doc["commands"]!.AsArray().First(c => c!["key"]!.GetValue<string>() == "convert_to_deal")!.AsObject();
        var create = convert["effects"]!.AsArray().Cast<JsonObject>()
            .Single(e => e!["type"]!.GetValue<string>() == "createRecord");
        Assert.Equal("deal", create["entity"]!.GetValue<string>());
        Assert.Equal("{{record.title}}", create["set"]!["title"]!.GetValue<string>());
    }

    [Fact]
    public void Stage_probability_stays_in_sync_via_field_changed_workflows()
    {
        var doc = (JsonObject)Reference();
        var stageWorkflows = doc["workflows"]!.AsArray().Cast<JsonObject>()
            .Where(w => w!["trigger"]!["field"]?.GetValue<string>() == "stage").ToList();
        Assert.Equal(5, stageWorkflows.Count);
        Assert.All(stageWorkflows, w =>
        {
            Assert.Equal("field.changed", w["trigger"]!["event"]!.GetValue<string>());
            var set = w["effects"]![0]!["set"]!.AsObject();
            Assert.True(set.ContainsKey("probability"));
        });
    }

    private static void Collect(JsonNode? blocks, string kind, List<JsonObject> hits)
    {
        if (blocks is not JsonArray arr) return;
        foreach (var bn in arr)
        {
            if (bn is not JsonObject b) continue;
            if (b["kind"]?.GetValue<string>() == kind) hits.Add(b);
            Collect(b["blocks"], kind, hits);
            if (b["columns"] is JsonArray cols) foreach (var c in cols) Collect(c, kind, hits);
            if (b["tabs"] is JsonArray tabs) foreach (var t in tabs) Collect(t?["blocks"], kind, hits);
        }
    }
}

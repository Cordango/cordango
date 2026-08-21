// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Compiler.Tests;

/// <summary>BehaviorMerge: lowering the behavior pass's emission onto a frozen pure-domain
/// definition — section copy, entityPatches → role:'status' select creation, deterministic
/// states-as-source-of-truth normalization, and the repair-guiding issues. Plus the split
/// pipeline's foundational gate property: a PURE domain document (no behavior plane at all)
/// is gate-clean.</summary>
public class BehaviorMergeTests
{
    private static JsonNode PureDomain() => JsonNode.Parse("""
    {
      "schemaVersion": "1.0", "key": "support", "name": "Support", "version": "1.0.0",
      "entities": [
        { "key": "ticket", "label": "Ticket", "displayField": "subject",
          "fields": [
            { "key": "subject", "label": "Subject", "type": "text", "required": true }
          ] }
      ]
    }
    """)!;

    private static JsonNode Behavior() => JsonNode.Parse("""
    {
      "authoringVersion": "1.0",
      "entityPatches": [
        { "entity": "ticket", "statusField": { "key": "stage", "label": "Stage" } }
      ],
      "processes": [
        { "key": "ticket_flow", "entity": "ticket", "stateField": "stage",
          "initialState": "open",
          "states": [
            { "key": "open", "label": "Open" },
            { "key": "closed", "label": "Closed" }
          ],
          "transitions": [
            { "key": "close", "from": ["open"], "to": "closed", "command": "close_ticket" }
          ] }
      ],
      "commands": [
        { "key": "close_ticket", "label": "Close ticket", "entity": "ticket", "effects": [] }
      ]
    }
    """)!;

    private static JsonObject Entity(JsonNode merged, string key) =>
        merged["entities"]!.AsArray().OfType<JsonObject>().First(e => e["key"]!.GetValue<string>() == key);

    private static JsonObject? Field(JsonNode merged, string entity, string key) =>
        (Entity(merged, entity)["fields"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(f => f["key"]!.GetValue<string>() == key);

    [Fact]
    public void A_pure_domain_document_is_gate_clean()
    {
        Assert.Empty(Gate.Validate(PureDomain()));
    }

    [Fact]
    public void Happy_path_merges_sections_lowers_the_status_field_and_passes_the_gate()
    {
        var merged = BehaviorMerge.Apply(PureDomain(), Behavior(), out var issues);
        Assert.Empty(issues);
        Assert.Single(merged["processes"]!.AsArray());
        Assert.Single(merged["commands"]!.AsArray());
        Assert.Null(merged["entityPatches"]);   // authoring-only, never stored

        var stage = Field(merged, "ticket", "stage");
        Assert.NotNull(stage);
        Assert.Equal("select", stage!["type"]!.GetValue<string>());
        Assert.Equal("status", stage["role"]!.GetValue<string>());
        Assert.Equal("Stage", stage["label"]!.GetValue<string>());
        Assert.Null(stage["options"]);   // the process's states ARE the options

        Assert.Empty(Gate.Validate(merged));
    }

    [Fact]
    public void A_domain_authored_select_is_normalized_to_process_governance()
    {
        // The domain pass leaked a stage select WITH options and a default — the merge strips
        // both deterministically instead of burning a repair round on the gate error.
        var domain = PureDomain();
        (Entity(domain, "ticket")["fields"] as JsonArray)!.Add(JsonNode.Parse("""
            { "key": "stage", "label": "Stage", "type": "select", "default": "open",
              "options": [ { "value": "open", "label": "Open" }, { "value": "closed", "label": "Closed" } ] }
        """));
        var merged = BehaviorMerge.Apply(domain, Behavior(), out var issues);
        Assert.Empty(issues);
        var stage = Field(merged, "ticket", "stage")!;
        Assert.Equal("status", stage["role"]!.GetValue<string>());
        Assert.Null(stage["options"]);
        Assert.Null(stage["default"]);
        Assert.Empty(Gate.Validate(merged));
    }

    [Fact]
    public void Wrong_authoring_version_is_an_issue()
    {
        var behavior = Behavior();
        behavior["authoringVersion"] = "0.9";
        BehaviorMerge.Apply(PureDomain(), behavior, out var issues);
        Assert.Contains(issues, i => i.Contains("authoringVersion"));
    }

    [Fact]
    public void Patch_for_an_unknown_entity_is_an_issue()
    {
        var behavior = Behavior();
        behavior["entityPatches"]![0]!["entity"] = "ghost";
        BehaviorMerge.Apply(PureDomain(), behavior, out var issues);
        Assert.Contains(issues, i => i.Contains("unknown entity 'ghost'"));
    }

    [Fact]
    public void Patch_targeting_a_non_select_field_is_an_issue()
    {
        var behavior = Behavior();
        behavior["entityPatches"]![0]!["statusField"]!["key"] = "subject";   // a text field
        BehaviorMerge.Apply(PureDomain(), behavior, out var issues);
        Assert.Contains(issues, i => i.Contains("not a 'select'"));
    }

    [Fact]
    public void Duplicate_patches_are_an_issue()
    {
        var behavior = Behavior();
        behavior["entityPatches"]!.AsArray().Add(behavior["entityPatches"]![0]!.DeepClone());
        BehaviorMerge.Apply(PureDomain(), behavior, out var issues);
        Assert.Contains(issues, i => i.Contains("duplicate"));
    }

    [Fact]
    public void A_process_governing_a_missing_field_gets_an_entityPatches_hint()
    {
        var behavior = Behavior();
        behavior.AsObject().Remove("entityPatches");   // process still names stateField 'stage'
        BehaviorMerge.Apply(PureDomain(), behavior, out var issues);
        Assert.Contains(issues, i => i.Contains("entityPatches") && i.Contains("stage"));
    }

    [Fact]
    public void A_non_object_behavior_document_is_rejected_without_touching_the_domain()
    {
        var domain = PureDomain();
        var merged = BehaviorMerge.Apply(domain, JsonNode.Parse("[]"), out var issues);
        Assert.Contains(issues, i => i.Contains("not an object"));
        Assert.Equal(domain.ToJsonString(), merged.ToJsonString());
    }
}

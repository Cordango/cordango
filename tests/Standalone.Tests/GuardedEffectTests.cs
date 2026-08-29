// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Definition;
using Cordango.SourceGen;
using Cordango.SourceGen.DotNetVue.Emit;
using Cordango.SourceGen.DotNetVue.Model;
using Cordango.Standalone.Conditions;
using Cordango.Standalone.Workflows;

namespace Cordango.Standalone.Tests;

public class GuardedEffectTests
{
    [Fact]
    public void An_effect_with_no_guard_is_written_exactly_as_before()
    {
        var written = Emit("""
            { "type": "notify", "to": "a@b.com", "title": "Hi" }
            """);

        Assert.Equal("new NotifyEffect(\"a@b.com\", \"Hi\", null, null)", written);
    }

    [Fact]
    public void A_guard_is_attached_as_an_initialiser()
    {
        var written = Emit("""
            { "type": "notify", "to": "a@b.com", "title": "Hi",
              "when": { "field": "state", "operator": "eq", "value": "done" } }
            """);

        Assert.NotNull(written);
        Assert.Contains("{ When = Condition.Leaf(\"state\", \"eq\", \"done\") }", written, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_effect_kind_takes_a_guard()
    {
        foreach (var body in new[]
        {
            """ "type": "notify", "to": "a@b.com", "title": "Hi" """,
            """ "type": "updateRecord", "set": { "state": "done" } """,
            """ "type": "createRecord", "entity": "thing", "set": { "name": "x" } """,
            """ "type": "deleteRecord", "target": { "field": "parent" } """,
        })
        {
            var written = Emit($$"""
                { {{body}}, "when": { "field": "state", "operator": "eq", "value": "done" } }
                """);

            Assert.NotNull(written);
            Assert.Contains("When = Condition.Leaf", written, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_guard_that_cannot_be_written_takes_the_whole_effect()
    {
        var written = Emit("""
            { "type": "notify", "to": "a@b.com", "title": "Hi", "when": { "nonsense": true } }
            """);

        Assert.Null(written);
    }

    [Fact]
    public void A_delete_of_the_triggering_record_needs_no_target()
    {
        Assert.Equal("new DeleteRecordEffect()", Emit("""{ "type": "deleteRecord" }"""));
    }

    [Fact]
    public void A_delete_through_a_reference_carries_the_entity_it_resolves_to()
    {
        var written = Emit("""
            { "type": "deleteRecord", "target": { "field": "parent" } }
            """);

        Assert.NotNull(written);
        Assert.Contains("TargetField: \"parent\"", written, StringComparison.Ordinal);
        Assert.Contains("TargetEntity: \"thing\"", written, StringComparison.Ordinal);
    }

    [Fact]
    public void A_delete_through_a_field_that_names_no_entity_is_refused()
    {
        Assert.Null(Emit("""
            { "type": "deleteRecord", "target": { "field": "name" } }
            """));
    }

    [Fact]
    public void The_runtime_skips_an_effect_whose_guard_is_false()
    {
        var record = new JsonObject { ["state"] = "open" };

        Assert.False(ConditionEvaluator.Evaluate(
            new DeleteRecordEffect { When = Condition.Leaf("state", "eq", "done") }.When,
            record, null, DateTimeOffset.UnixEpoch));

        Assert.True(ConditionEvaluator.Evaluate(
            new DeleteRecordEffect().When, record, null, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void A_created_token_resolves_to_the_new_records_field()
    {
        var made = new JsonObject { ["id"] = "abc123", ["name"] = "Parent" };

        var filled = ValueTokens.Fill(
            "{{created.id}} / {{created.name}}", null, null, DateTimeOffset.UnixEpoch,
            created: field => (string?)made[field]);

        Assert.Equal("abc123 / Parent", filled);
    }

    [Fact]
    public void A_created_token_with_nothing_created_is_left_as_written()
    {
        var filled = ValueTokens.Fill(
            "{{created.id}}", null, null, DateTimeOffset.UnixEpoch);

        Assert.Equal("{{created.id}}", filled);
    }

    [Fact]
    public void A_created_token_does_not_shadow_the_record()
    {
        var record = new JsonObject { ["id"] = "outer" };
        var made = new JsonObject { ["id"] = "inner" };

        var filled = ValueTokens.Fill(
            "{{record.id}} then {{created.id}}", null, null, DateTimeOffset.UnixEpoch,
            record: field => (string?)record[field],
            created: field => (string?)made[field]);

        Assert.Equal("outer then inner", filled);
    }

    private static string? Emit(string effect)
    {
        var entity = new JsonObject
        {
            ["key"] = "thing",
            ["label"] = "Thing",
            ["fields"] = new JsonArray(
                new JsonObject { ["key"] = "name", ["label"] = "Name", ["type"] = "text" },
                new JsonObject { ["key"] = "state", ["label"] = "State", ["type"] = "text" },
                new JsonObject
                {
                    ["key"] = "parent",
                    ["label"] = "Parent",
                    ["type"] = "reference",
                    ["targetEntity"] = "thing",
                }),
        };

        var manifest = new JsonObject
        {
            ["key"] = "app",
            ["name"] = "App",
            ["entities"] = new JsonArray(entity.DeepClone()),
        };

        var app = AppModel.From(new CompiledAppArtifact(
            manifest, manifest, "unhashed", new CompilerInfo("test", "1")));

        return WorkflowEmitter.Effect(app, "thing", (JsonObject)JsonNode.Parse(effect)!);
    }
}

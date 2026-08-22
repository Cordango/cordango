// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Compiler.Tests;

public class OptionsFilterTests
{
    private static JsonObject Definition(JsonNode? optionsFilter, string fieldType = "reference",
        string? targetApp = null)
    {
        var refField = new JsonObject
        {
            ["key"] = "calendar",
            ["label"] = "Calendar",
            ["type"] = fieldType,
        };
        if (fieldType == "reference")
        {
            refField["targetEntity"] = targetApp == "platform" ? "person" : "calendar_source";
            if (targetApp is not null) refField["targetApp"] = targetApp;
        }
        if (optionsFilter is not null) refField["optionsFilter"] = optionsFilter;

        return new JsonObject
        {
            ["schemaVersion"] = "2.0",
            ["key"] = "diary",
            ["name"] = "Diary",
            ["version"] = "1.0.0",
            ["entities"] = new JsonArray
            {
                new JsonObject
                {
                    ["key"] = "calendar_source",
                    ["label"] = "Calendar",
                    ["labelPlural"] = "Calendars",
                    ["displayField"] = "name",
                    ["fields"] = new JsonArray
                    {
                        new JsonObject { ["key"] = "name", ["label"] = "Name", ["type"] = "text", ["required"] = true },
                        new JsonObject
                        {
                            ["key"] = "owner", ["label"] = "Owner", ["type"] = "reference",
                            ["targetApp"] = "platform", ["targetEntity"] = "person",
                        },
                    },
                },
                new JsonObject
                {
                    ["key"] = "event",
                    ["label"] = "Event",
                    ["labelPlural"] = "Events",
                    ["displayField"] = "title",
                    ["fields"] = new JsonArray
                    {
                        new JsonObject { ["key"] = "title", ["label"] = "Title", ["type"] = "text", ["required"] = true },
                        refField,
                    },
                },
            },
            ["pages"] = new JsonArray
            {
                new JsonObject
                {
                    ["key"] = "events",
                    ["label"] = "Events",
                    ["entity"] = "event",
                    ["blocks"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["kind"] = "table",
                            ["fields"] = new JsonArray { "title" },
                            ["source"] = new JsonObject { ["entity"] = "event" },
                        },
                    },
                },
            },
            ["roles"] = new JsonArray
            {
                new JsonObject
                {
                    ["key"] = "admin",
                    ["name"] = "Admin",
                    ["grants"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["entity"] = "event",
                            ["create"] = true, ["read"] = true, ["update"] = true, ["delete"] = true,
                        },
                        new JsonObject { ["entity"] = "calendar_source", ["read"] = true },
                    },
                },
            },
        };
    }

    private static JsonArray Mine(string field = "owner") => new()
    {
        new JsonObject { ["field"] = field, ["operator"] = "eq", ["value"] = "{{actor.id}}" },
    };

    private static List<string> Errors(JsonNode? optionsFilter, string fieldType = "reference",
        string? targetApp = null) =>
        Gate.Validate(Definition(optionsFilter, fieldType, targetApp)).ToList();

    private static bool Complains(List<string> errors) =>
        errors.Any(e => e.Contains("optionsFilter", StringComparison.Ordinal));

    [Fact]
    public void Scoping_a_picker_to_the_acting_user_passes()
    {
        Assert.False(Complains(Errors(Mine())));
    }

    [Fact]
    public void Omitting_it_entirely_is_fine()
    {
        Assert.False(Complains(Errors(null)));
    }

    [Fact]
    public void A_leaf_naming_a_field_the_target_does_not_have_is_an_error_naming_it()
    {
        var errors = Errors(Mine("belongs_to"));
        Assert.Contains(errors, e => e.Contains("optionsFilter") && e.Contains("belongs_to")
                                  && e.Contains("calendar_source"));
    }

    [Fact]
    public void A_field_of_the_declaring_entity_rather_than_the_target_is_still_an_error()
    {
        Assert.Contains(Errors(Mine("title")), e => e.Contains("optionsFilter") && e.Contains("title"));
    }

    [Fact]
    public void A_path_hop_is_refused()
    {
        var errors = Errors(new JsonArray
        {
            new JsonObject { ["path"] = "owner.email", ["operator"] = "eq", ["value"] = "x@y.test" },
        });
        Assert.Contains(errors, e => e.Contains("optionsFilter") && e.Contains("owner.email"));
    }

    [Fact]
    public void It_is_refused_on_a_field_that_is_not_a_reference()
    {
        Assert.Contains(Errors(Mine(), fieldType: "text"),
            e => e.Contains("optionsFilter") && e.Contains("not a reference"));
    }

    [Fact]
    public void A_target_the_gate_cannot_see_is_left_alone()
    {
        Assert.False(Complains(Errors(Mine("anything_at_all"), targetApp: "platform")));
    }

    private static List<string> InputErrors(string type, string input)
    {
        var def = Definition(null);
        var events = (JsonObject)((JsonArray)def["entities"]!)[1]!;
        ((JsonArray)events["fields"]!).Add(new JsonObject
        {
            ["key"] = "probe", ["label"] = "Probe", ["type"] = type, ["input"] = input,
        });
        return Gate.Validate(def).Where(e => e.Contains("input", StringComparison.Ordinal)).ToList();
    }

    [Theory]
    [InlineData("text", "timezone")]
    [InlineData("text", "slug")]
    [InlineData("json", "weeklyHours")]
    public void An_input_on_the_type_it_was_built_for_passes(string type, string input)
    {
        Assert.Empty(InputErrors(type, input));
    }

    [Theory]
    [InlineData("integer", "timezone")]
    [InlineData("text", "weeklyHours")]
    [InlineData("json", "slug")]
    public void An_input_on_the_wrong_type_is_an_error_naming_both(string type, string input)
    {
        Assert.Contains(InputErrors(type, input), e => e.Contains(input) && e.Contains(type));
    }
}

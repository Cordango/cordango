// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Compiler.Tests;

/// <summary>
/// <c>field.optionsFilter</c> — which target records a reference picker OFFERS.
///
/// <para>Without it a picker lists every record of the target entity, which is the wrong question
/// whenever the target is personal: "which calendar?" beside forty colleagues' calendars is not a
/// choice anybody can make, and the ones belonging to other people are indistinguishable from your
/// own when they share a name.</para>
///
/// <para>The failure mode this gate exists for is quiet. A leaf naming a field the target does not
/// have filters on a value that is never present, so the picker comes back EMPTY — and an empty
/// picker reads as "nobody has one yet", which is exactly what a correct-but-unpopulated picker
/// looks like. Nothing downstream can tell the two apart, so it has to fail here.</para>
/// </summary>
public class OptionsFilterTests
{
    /// <summary>An app with a personal `calendar` the event points at — the shape this was built for.</summary>
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

    /// <summary>
    /// The leaf's `field` is a key on the TARGET, not on the entity declaring the reference. Getting
    /// that backwards is the obvious mistake, and it has to be caught here rather than producing an
    /// empty picker: `title` is a real field of `event`, so nothing about the definition looks wrong.
    /// </summary>
    [Fact]
    public void A_field_of_the_declaring_entity_rather_than_the_target_is_still_an_error()
    {
        Assert.Contains(Errors(Mine("title")), e => e.Contains("optionsFilter") && e.Contains("title"));
    }

    /// <summary>A picker filters over rows it has already loaded, and a hop through a reference is
    /// not among them — so `path` has to be refused rather than silently matching nothing.</summary>
    [Fact]
    public void A_path_hop_is_refused()
    {
        var errors = Errors(new JsonArray
        {
            new JsonObject { ["path"] = "owner.email", ["operator"] = "eq", ["value"] = "x@y.test" },
        });
        Assert.Contains(errors, e => e.Contains("optionsFilter") && e.Contains("owner.email"));
    }

    /// <summary>A select's choices are its `options`. Two spellings for "what may be picked" would be
    /// two places to look, and only one of them would ever be honoured.</summary>
    [Fact]
    public void It_is_refused_on_a_field_that_is_not_a_reference()
    {
        Assert.Contains(Errors(Mine(), fieldType: "text"),
            e => e.Contains("optionsFilter") && e.Contains("not a reference"));
    }

    /// <summary>
    /// A platform or core target is resolved at render time and its field list is not knowable here.
    /// Guessing at it would fail valid apps, so an unresolvable target is left alone rather than
    /// rejected — the honest answer for a shape the gate genuinely cannot see.
    /// </summary>
    [Fact]
    public void A_target_the_gate_cannot_see_is_left_alone()
    {
        Assert.False(Complains(Errors(Mine("anything_at_all"), targetApp: "platform")));
    }

    // ---- field.input ---------------------------------------------------------------------------
    //
    // A richer control only knows how to read and write one storage shape. Put the opening-hours grid
    // on a text column and it degrades to a plain box — silently, and only for whoever happens to open
    // that form, which is the worst place to find out.

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

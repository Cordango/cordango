// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Compiler.Tests;

public class CompositeUniqueTests
{
    private static JsonObject Definition(JsonNode? unique)
    {
        var entity = new JsonObject
        {
            ["key"] = "booking",
            ["label"] = "Booking",
            ["labelPlural"] = "Bookings",
            ["displayField"] = "summary",
            ["fields"] = new JsonArray
            {
                new JsonObject { ["key"] = "summary", ["label"] = "Summary", ["type"] = "text", ["required"] = true },
                new JsonObject { ["key"] = "page", ["label"] = "Page", ["type"] = "text" },
                new JsonObject { ["key"] = "starts_at", ["label"] = "Starts", ["type"] = "datetime" },
            },
        };
        if (unique is not null) entity["unique"] = unique;

        return new JsonObject
        {
            ["schemaVersion"] = "2.0",
            ["key"] = "bookings_app",
            ["name"] = "Bookings",
            ["version"] = "1.0.0",
            ["entities"] = new JsonArray { entity },
            ["pages"] = new JsonArray
            {
                new JsonObject
                {
                    ["key"] = "list",
                    ["label"] = "Bookings",
                    ["entity"] = "booking",
                    ["blocks"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["kind"] = "table",
                            ["fields"] = new JsonArray { "summary" },
                            ["source"] = new JsonObject { ["entity"] = "booking" },
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
                            ["entity"] = "booking",
                            ["create"] = true, ["read"] = true, ["update"] = true, ["delete"] = true,
                        },
                    },
                },
            },
        };
    }

    private static List<string> Errors(JsonNode? unique) => Gate.Validate(Definition(unique)).ToList();

    [Fact]
    public void A_valid_combination_passes()
    {
        var errors = Errors(new JsonArray { new JsonArray { "page", "starts_at" } });
        Assert.DoesNotContain(errors, e => e.Contains("unique", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Omitting_it_entirely_is_fine()
    {
        Assert.DoesNotContain(Errors(null), e => e.Contains("unique", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_key_that_is_not_a_field_is_an_error_naming_the_key()
    {
        var errors = Errors(new JsonArray { new JsonArray { "page", "nope" } });
        Assert.Contains(errors, e => e.Contains("unique") && e.Contains("nope"));
    }

    [Fact]
    public void A_repeated_field_is_an_error()
    {
        var errors = Errors(new JsonArray { new JsonArray { "page", "page" } });
        Assert.Contains(errors, e => e.Contains("unique") && e.Contains("repeats"));
    }

    [Fact]
    public void A_single_field_combination_is_refused_and_points_at_the_field_flag()
    {
        var errors = Gate.Validate(Definition(new JsonArray { new JsonArray { "page" } })).ToList();
        Assert.Contains(errors, e => e.Contains("unique", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Three_fields_are_allowed()
    {
        var errors = Errors(new JsonArray { new JsonArray { "page", "starts_at", "summary" } });
        Assert.DoesNotContain(errors, e => e.Contains("unique", StringComparison.OrdinalIgnoreCase));
    }
}

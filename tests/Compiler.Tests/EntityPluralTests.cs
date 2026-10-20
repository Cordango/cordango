// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Compile;

namespace Cordango.Compiler.Tests;

public class EntityPluralTests
{
    private static string PluralOf(string label, string? plural = null)
    {
        var entity = new JsonObject
        {
            ["key"] = "thing",
            ["label"] = label,
            ["fields"] = new JsonArray(new JsonObject { ["key"] = "name", ["label"] = "Name", ["type"] = "text" }),
        };
        if (plural is not null) entity["labelPlural"] = plural;
        var manifest = AppCompiler.Compile(
            new JsonObject { ["key"] = "app", ["name"] = "App", ["entities"] = new JsonArray(entity) },
            "app1", DateTimeOffset.UnixEpoch);
        return manifest["entities"]![0]!["labelPlural"]!.GetValue<string>();
    }

    [Theory]
    [InlineData("Task", "Tasks")]
    [InlineData("Booking Policy", "Booking Policies")]
    [InlineData("Day", "Days")]
    [InlineData("Status", "Statuses")]
    [InlineData("Address", "Addresses")]
    [InlineData("Tax", "Taxes")]
    [InlineData("Batch", "Batches")]
    [InlineData("Settings", "Settings")]
    [InlineData("News", "News")]
    public void An_omitted_plural_is_the_labels_english_plural(string label, string expected)
    {
        Assert.Equal(expected, PluralOf(label));
    }

    [Fact]
    public void An_authored_plural_is_kept()
    {
        Assert.Equal("People", PluralOf("Person", "People"));
    }
}

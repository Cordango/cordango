// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Definition;
using Cordango.SourceGen;
using Cordango.SourceGen.Common;

namespace Cordango.Standalone.Tests;

public class ViewNewButtonTests
{
    [Fact]
    public void A_view_that_says_nothing_offers_the_button()
    {
        Assert.True(View(null).NewButton);
    }

    [Fact]
    public void A_view_may_turn_it_off()
    {
        Assert.False(View(false).NewButton);
    }

    [Fact]
    public void A_view_may_say_yes_explicitly()
    {
        Assert.True(View(true).NewButton);
    }

    [Fact]
    public void The_contract_lets_an_author_write_it()
    {
        var config = ComponentCatalog.Find("view.table")!.ConfigSchema;

        Assert.NotNull(config["properties"]!["newButton"]);
    }

    [Fact]
    public void A_suppressed_view_is_not_counted_as_the_apps_create_path()
    {
        var withButton = Document(newButton: null);
        var without = Document(newButton: false);

        Assert.DoesNotContain("thing", DesignDefaults.EntitiesWithoutCreatePath(withButton));
        Assert.Contains("thing", DesignDefaults.EntitiesWithoutCreatePath(without));
    }

    private static ViewModel View(bool? newButton)
    {
        var json = new JsonObject
        {
            ["key"] = "things",
            ["label"] = "Things",
            ["type"] = "table",
            ["entity"] = "thing",
        };

        if (newButton is { } flag) json["config"] = new JsonObject { ["newButton"] = flag };

        return new ViewModel(json);
    }

    private static JsonObject Document(bool? newButton)
    {
        var view = new JsonObject
        {
            ["key"] = "things",
            ["label"] = "Things",
            ["type"] = "table",
            ["entity"] = "thing",
        };

        if (newButton is { } flag) view["config"] = new JsonObject { ["newButton"] = flag };

        return new JsonObject
        {
            ["schemaVersion"] = "2.0",
            ["key"] = "app",
            ["name"] = "App",
            ["version"] = "1.0.0",
            ["entities"] = new JsonArray(new JsonObject
            {
                ["key"] = "thing",
                ["label"] = "Thing",
                ["displayField"] = "name",
                ["fields"] = new JsonArray(
                    new JsonObject { ["key"] = "name", ["label"] = "Name", ["type"] = "text" }),
            }),
            ["views"] = new JsonArray(view),
            ["pages"] = new JsonArray(new JsonObject
            {
                ["key"] = "home",
                ["label"] = "Home",
                ["entity"] = "thing",
                ["blocks"] = new JsonArray(new JsonObject
                {
                    ["kind"] = "view",
                    ["view"] = "things",
                }),
            }),
        };
    }
}

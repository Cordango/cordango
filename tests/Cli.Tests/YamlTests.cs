// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Cli.Workspace;

namespace Cordango.Cli.Tests;

public class YamlTests
{
    [Theory]
    [InlineData(" yr")]
    [InlineData("mo ")]
    [InlineData("on")]
    [InlineData("off")]
    [InlineData("yes")]
    [InlineData("no")]
    [InlineData("No")]
    [InlineData("y")]
    [InlineData("true")]
    [InlineData("2.0")]
    [InlineData("10.0.0")]
    [InlineData("1e5")]
    [InlineData("#0f766e")]
    [InlineData("%")]
    [InlineData("- dash")]
    [InlineData("key: value")]
    [InlineData("null")]
    [InlineData("~")]
    [InlineData("")]
    [InlineData("Werkstudent (Vollzeit) — über 20 Std.")]
    [InlineData("{{actor.name}} asked for changes")]
    public void A_string_survives_as_a_string(string value)
    {
        var (document, error) = Yaml.Read(Yaml.Write(new JsonObject { ["v"] = value }));

        Assert.Null(error);
        Assert.Equal(value, (string?)document!["v"]);
    }

    [Theory]
    [InlineData("on")]
    [InlineData("no")]
    [InlineData("yes")]
    [InlineData("off")]
    [InlineData("true")]
    [InlineData("2.0")]
    public void A_KEY_survives_too(string key)
    {
        var (document, error) = Yaml.Read(Yaml.Write(new JsonObject { [key] = "record.created" }));

        Assert.Null(error);
        Assert.True(document!.ContainsKey(key), $"the key '{key}' did not survive: {Yaml.Write(document)}");
    }

    [Fact]
    public void Numbers_keep_their_exact_text()
    {
        var original = (JsonObject)JsonNode.Parse(
            """{"int":1,"decimal":1.0,"negative":-2.50,"big":1000000,"zero":0.0}""")!;

        var (document, error) = Yaml.Read(Yaml.Write(original));

        Assert.Null(error);
        Assert.Equal(original.ToJsonString(), document!.ToJsonString());
    }

    [Fact]
    public void Booleans_and_nulls_stay_themselves()
    {
        var original = (JsonObject)JsonNode.Parse("""{"t":true,"f":false,"n":null}""")!;

        var (document, error) = Yaml.Read(Yaml.Write(original));

        Assert.Null(error);
        Assert.Equal(original.ToJsonString(), document!.ToJsonString());
    }

    [Fact]
    public void Key_order_is_preserved_because_it_is_what_makes_a_file_readable()
    {
        var original = new JsonObject
        {
            ["entity"] = "scenario", ["label"] = "Scenario", ["plural"] = "Scenarios",
            ["display"] = "name", ["fields"] = new JsonObject { ["b"] = 1, ["a"] = 2 },
        };

        var (document, _) = Yaml.Read(Yaml.Write(original));

        Assert.Equal(
            new[] { "entity", "label", "plural", "display", "fields" },
            document!.Select(p => p.Key));
        Assert.Equal(new[] { "b", "a" }, (document["fields"] as JsonObject)!.Select(p => p.Key));
    }

    [Fact]
    public void Sequences_are_indented_under_their_key()
    {
        var text = Yaml.Write(new JsonObject
        {
            ["options"] = new JsonArray(new JsonObject { ["value"] = "junior" }),
        });

        Assert.Contains("options:\n  - value: junior", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Line_endings_are_LF_regardless_of_platform()
    {
        var text = Yaml.Write(new JsonObject { ["a"] = "1", ["b"] = "2" });

        Assert.DoesNotContain('\r', text);
        Assert.EndsWith("\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_broken_file_is_reported_rather_than_thrown()
    {
        var (document, error) = Yaml.Read("entity: scenario\n  bad: indent\n");

        Assert.Null(document);
        Assert.NotNull(error);
    }
}

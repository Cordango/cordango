// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.SourceGen.DotNetVue;
using Cordango.TestCorpus;

namespace Cordango.Compatibility.Tests;

public class StandaloneCompatibilityTests
{
    private static readonly GeneratorCapabilities Standalone = new DotNetVueGenerator().Capabilities;

    private static readonly IReadOnlySet<string> PlaceAnAuditBlock =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "ventures.appdef.json", "budget-planner.appdef.json",
        };

    public static TheoryData<string> Applications()
    {
        var data = new TheoryData<string>();
        foreach (var path in Corpus.SemanticPaths()) data.Add(path);
        return data;
    }

    [Theory]
    [MemberData(nameof(Applications))]
    public void The_standalone_target_builds_this_application(string path)
    {
        var found = Validate(JsonNode.Parse(File.ReadAllText(path))!.AsObject());
        var name = Path.GetFileName(path);

        if (PlaceAnAuditBlock.Contains(name))
        {
            Assert.NotEmpty(found);
            Assert.All(found, d => Assert.Equal(DiagnosticCodes.HistoryBlock, d.Code));
            return;
        }

        Assert.True(found.Count == 0,
            $"{name} was expected to build standalone but reported:\n  "
            + string.Join("\n  ", found.Select(d => d.ToString())));
    }

    [Theory]
    [MemberData(nameof(EveryRefusal))]
    public void A_refusal_names_the_platform_and_offers_a_way_forward(string label, Diagnostic found)
    {
        Assert.Contains("Cordango Platform", found.Message, StringComparison.Ordinal);

        Assert.True(
            found.Message.Contains("to build standalone", StringComparison.OrdinalIgnoreCase),
            $"{label}: says what the platform does but not how to build standalone anyway");

        Assert.True(
            found.Message.Contains("run this application on Cordango Platform", StringComparison.OrdinalIgnoreCase),
            $"{label}: does not offer the platform as the other way forward");
    }

    public static TheoryData<string, Diagnostic> EveryRefusal()
    {
        var data = new TheoryData<string, Diagnostic>();

        void Add(string label, JsonObject definition) =>
            data.Add(label, Validate(definition).Single());

        Add("history block", WithPage(new JsonObject { ["kind"] = "history" }));
        Add("relatedApps block", WithPage(new JsonObject { ["kind"] = "relatedApps" }));
        Add("cross-app reference", WithField(new JsonObject
        {
            ["key"] = "customer",
            ["type"] = "reference",
            ["targetApp"] = "crm",
            ["targetEntity"] = "customer",
        }));
        Add("unmapped platform entity", WithField(new JsonObject
        {
            ["key"] = "thing",
            ["type"] = "reference",
            ["targetApp"] = "platform",
            ["targetEntity"] = "workspace",
        }));

        var enrich = Minimal();
        enrich["commands"] = new JsonArray
        {
            new JsonObject
            {
                ["key"] = "refresh",
                ["entity"] = "thing",
                ["effects"] = new JsonArray { new JsonObject { ["type"] = "enrich" } },
            },
        };
        Add("enrich effect", enrich);

        return data;
    }

    [Fact]
    public void The_audit_refusal_describes_what_the_trail_would_give_you()
    {
        var one = Assert.Single(Validate(WithPage(new JsonObject { ["kind"] = "history" })));

        Assert.Equal(DiagnosticCodes.HistoryBlock, one.Code);
        Assert.Contains("audit trail", one.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("who changed it", one.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_whole_corpus_is_actually_being_checked()
    {
        Assert.True(Corpus.SemanticPaths().Count >= 15,
            $"expected the full corpus, got {Corpus.SemanticPaths().Count}");
    }

    [Fact]
    public void A_reference_into_another_installed_application_is_refused()
    {
        var found = Validate(WithField(new JsonObject
        {
            ["key"] = "customer",
            ["type"] = "reference",
            ["targetApp"] = "crm",
            ["targetEntity"] = "customer",
        }));

        var one = Assert.Single(found);
        Assert.Equal(DiagnosticCodes.CrossAppReference, one.Code);

        Assert.Contains("People, Organizations", one.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("platform", "person")]
    [InlineData("platform", "department")]
    [InlineData("platform", "group")]
    [InlineData("core_organizations", "organization")]
    [InlineData("core_organizations", "contact")]
    public void A_platform_primitive_maps_to_its_local_equivalent(string app, string entity)
    {
        var found = Validate(WithField(new JsonObject
        {
            ["key"] = "owner",
            ["type"] = "reference",
            ["targetApp"] = app,
            ["targetEntity"] = entity,
        }));

        Assert.Empty(found);
    }

    [Fact]
    public void A_platform_entity_with_no_local_equivalent_is_refused()
    {
        var found = Validate(WithField(new JsonObject
        {
            ["key"] = "thing",
            ["type"] = "reference",
            ["targetApp"] = "platform",
            ["targetEntity"] = "workspace",
        }));

        Assert.Equal(DiagnosticCodes.UnsupportedPlatformTarget, Assert.Single(found).Code);
    }

    [Fact]
    public void The_related_apps_block_is_refused()
    {
        var found = Validate(WithPage(new JsonObject { ["kind"] = "relatedApps" }));

        var one = Assert.Single(found);
        Assert.Equal(DiagnosticCodes.RelatedAppsBlock, one.Code);
    }

    [Fact]
    public void The_enrich_effect_is_refused()
    {
        var definition = Minimal();
        definition["commands"] = new JsonArray
        {
            new JsonObject
            {
                ["key"] = "refresh",
                ["entity"] = "thing",
                ["effects"] = new JsonArray { new JsonObject { ["type"] = "enrich" } },
            },
        };

        var found = Validate(definition);
        Assert.Equal(DiagnosticCodes.EnrichEffect, Assert.Single(found).Code);
    }

    [Fact]
    public void An_ordered_series_and_its_expressions_are_in_scope()
    {
        var definition = Minimal();
        var entity = definition["entities"]!.AsArray()[0]!.AsObject();
        entity["series"] = new JsonObject { ["partition"] = "plan" };
        entity["fields"]!.AsArray().Add(new JsonObject
        {
            ["key"] = "active",
            ["type"] = "integer",
            ["computed"] = new JsonObject { ["expr"] = "prev(active) + joined - churned" },
        });
        entity["fields"]!.AsArray().Add(new JsonObject
        {
            ["key"] = "spend_to_date",
            ["type"] = "money",
            ["computed"] = new JsonObject
            {
                ["rollup"] = new JsonObject
                {
                    ["entity"] = "cost_line",
                    ["via"] = "plan",
                    ["window"] = new JsonObject { ["at"] = "month" },
                },
            },
        });

        Assert.Empty(Validate(definition));
    }

    [Theory]
    [InlineData("gantt")]
    [InlineData("timeline")]
    [InlineData("orgchart")]
    [InlineData("intake")]
    [InlineData("answers")]
    [InlineData("externalEmbed")]
    [InlineData("widgets")]
    public void This_screen_is_in_scope(string kind)
    {
        Assert.Empty(Validate(WithPage(new JsonObject { ["kind"] = kind })));
    }

    [Fact]
    public void Every_refusal_points_at_the_definition_and_explains_itself()
    {
        var definition = WithPage(new JsonObject { ["kind"] = "relatedApps" });

        foreach (var found in Validate(definition))
        {
            Assert.StartsWith("$.", found.JsonPath, StringComparison.Ordinal);
            Assert.True(found.Message.Length > 40, $"{found.Code} says only '{found.Message}'");
        }
    }

    private static IReadOnlyList<Diagnostic> Validate(JsonObject definition) =>
        TargetValidator.Validate(definition, Standalone);

    private static JsonObject Minimal() => new()
    {
        ["schemaVersion"] = "2.0",
        ["key"] = "sample",
        ["name"] = "Sample",
        ["version"] = "1.0.0",
        ["entities"] = new JsonArray
        {
            new JsonObject
            {
                ["key"] = "thing",
                ["kind"] = "collection",
                ["fields"] = new JsonArray { new JsonObject { ["key"] = "name", ["type"] = "text" } },
            },
        },
    };

    private static JsonObject WithField(JsonObject field)
    {
        var definition = Minimal();
        definition["entities"]!.AsArray()[0]!["fields"]!.AsArray().Add(field);
        return definition;
    }

    private static JsonObject WithPage(JsonObject block)
    {
        var definition = Minimal();
        definition["pages"] = new JsonArray
        {
            new JsonObject { ["key"] = "home", ["blocks"] = new JsonArray { block } },
        };
        return definition;
    }
}

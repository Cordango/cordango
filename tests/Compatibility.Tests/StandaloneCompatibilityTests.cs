// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.SourceGen.DotNetVue;
using Cordango.TestCorpus;

namespace Cordango.Compatibility.Tests;

/// <summary>
/// Which real applications the standalone target can build. All of them, bar an audit block.
///
/// <para><b>That is a claim about the product, not a happy accident.</b> "One application" is a
/// statement about how many applications, not about how big or how clever one is allowed to be — an
/// ordered series, rollups over a declared window, a Gantt chart and an audit trail are all things a
/// single business application legitimately wants, and a standalone target that could not express
/// them would be selling a toy. The line is drawn somewhere else entirely: at features whose meaning
/// depends on OTHER installed applications, or on a model.</para>
///
/// <para>An earlier version of this suite pinned five applications as buildable-except-for-screens
/// and two as refused outright. That was a measurement of what had been implemented rather than a
/// decision about what should be, and it was wrong in the direction that matters — the flagship
/// Budget Planner, which exists to demonstrate the calculation plane, was on the refused list for
/// its ordered series. Every screen and the whole calculation plane are now in scope. Two
/// applications still stop at the audit block, and that one is a product line rather than a
/// gap.</para>
///
/// <para>The corpus is the same one the gate and the round-trip suites use, so an app added to
/// <c>tests/corpus/reference/</c> is enrolled here automatically.</para>
/// </summary>
public class StandaloneCompatibilityTests
{
    private static readonly GeneratorCapabilities Standalone = new DotNetVueGenerator().Capabilities;

    /// <summary>
    /// The two applications that place an audit block, and the only capability any corpus app asks
    /// for that this target does not have.
    ///
    /// <para>Record history is a platform capability rather than a missing emitter, so this is a
    /// commercial line and not a backlog item. Worth a named list because the day a third app
    /// appears here, somebody should have to notice.</para>
    ///
    /// <para><b>Reported here, not refused by a build.</b> This suite is what
    /// <c>cordango check --target standalone</c> answers: "can this target build all of it?" — and
    /// for these two the honest answer is no. A BUILD generates them anyway, leaves a card where the
    /// block was, and records the gap; one card on one screen is no reason to withhold an entire
    /// application. See <c>PartialBuildTests</c>.</para>
    /// </summary>
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
            // Refused for exactly one reason, and it is the platform one. Anything else appearing
            // here would be the target narrowing somewhere else, hidden behind an expected failure.
            Assert.NotEmpty(found);
            Assert.All(found, d => Assert.Equal(DiagnosticCodes.HistoryBlock, d.Code));
            return;
        }

        Assert.True(found.Count == 0,
            $"{name} was expected to build standalone but reported:\n  "
            + string.Join("\n  ", found.Select(d => d.ToString())));
    }

    /// <summary>
    /// EVERY refusal has to read as "this is a platform feature", not as "unsupported".
    ///
    /// <para>These are the only things a standalone build cannot do, and every one of them has the
    /// same answer available: run the application on Cordango Platform. A message that says
    /// "unsupported" and stops turns that into a bug report instead of a conversation, so the
    /// wording is asserted here rather than left to whoever edits the capability list next.</para>
    ///
    /// <para>The rule applies to all four equally. An earlier draft argued related-apps was
    /// different in kind, because with one application the question is unanswerable rather than
    /// merely unimplemented. That is true and it is useless to a reader: from their side both are
    /// "the platform does this and this build does not", and cross-application data is precisely
    /// what the platform is for.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryRefusal))]
    public void A_refusal_names_the_platform_and_offers_a_way_forward(string label, Diagnostic found)
    {
        Assert.Contains("Cordango Platform", found.Message, StringComparison.Ordinal);

        // Two ways forward, always: change the application, or change where it runs.
        Assert.True(
            found.Message.Contains("to build standalone", StringComparison.OrdinalIgnoreCase),
            $"{label}: says what the platform does but not how to build standalone anyway");

        Assert.True(
            found.Message.Contains("run this application on Cordango Platform", StringComparison.OrdinalIgnoreCase),
            $"{label}: does not offer the platform as the other way forward");
    }

    /// <summary>One case per thing a standalone build refuses. If a fifth appears, it lands here and
    /// has to meet the same bar.</summary>
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

    /// <summary>The audit refusal additionally has to say what the trail WOULD give them. It is the
    /// one refusal where the feature is invisible until you have it, so naming the platform without
    /// describing the capability would be an advert rather than an explanation.</summary>
    [Fact]
    public void The_audit_refusal_describes_what_the_trail_would_give_you()
    {
        var one = Assert.Single(Validate(WithPage(new JsonObject { ["kind"] = "history" })));

        Assert.Equal(DiagnosticCodes.HistoryBlock, one.Code);
        Assert.Contains("audit trail", one.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("who changed it", one.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The corpus has to be the real one for the test above to mean anything. A path typo
    /// answering "nothing to check" would pass every assertion in this class.</summary>
    [Fact]
    public void The_whole_corpus_is_actually_being_checked()
    {
        Assert.True(Corpus.SemanticPaths().Count >= 15,
            $"expected the full corpus, got {Corpus.SemanticPaths().Count}");
    }

    // ---- what remains out of scope, and why ----------------------------------------------------

    /// <summary>
    /// A reference into a DIFFERENT installed application.
    ///
    /// <para>Not a gap: a standalone build is one application, so there is no second one for the
    /// reference to resolve against. This is the boundary that actually separates the two products,
    /// and it is worth a test precisely because it is the one somebody would be tempted to paper
    /// over by generating a dangling id column.</para>
    /// </summary>
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

        // And it says what a standalone build DOES carry, so the reader can see whether pointing
        // the field somewhere local is an option before they go and buy anything.
        Assert.Contains("People, Organizations", one.Message, StringComparison.Ordinal);
    }

    /// <summary>The platform primitives DO map — that is what makes the same definition work in both
    /// places — so the test above must not be passing for the boring reason that every
    /// <c>targetApp</c> is refused.</summary>
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

    /// <summary>
    /// The related-apps block, which is the only screen that is not unbuilt but unmeaningful.
    ///
    /// <para>It asks which records in other installed applications point at this one. With one
    /// application there is nothing to look through, so generating an empty panel would be a
    /// screen that silently always says "nothing here".</para>
    /// </summary>
    [Fact]
    public void The_related_apps_block_is_refused()
    {
        var found = Validate(WithPage(new JsonObject { ["kind"] = "relatedApps" }));

        var one = Assert.Single(found);
        Assert.Equal(DiagnosticCodes.RelatedAppsBlock, one.Code);
    }

    /// <summary>Enrichment researches a company against the public web and files what it finds with
    /// evidence. It needs a model and a research pipeline, and a generated application deliberately
    /// contains neither.</summary>
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

    /// <summary>
    /// The calculation plane is IN scope, and this is the assertion that says so.
    ///
    /// <para>These four were excluded when the target was first declared, on the argument that they
    /// need the recompute cascade. They do — but so does the flagship example, and a calculation
    /// plane that only works on the hosted product is not the one being sold. This test fails the
    /// day somebody quietly narrows the target back.</para>
    /// </summary>
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

    /// <summary>Every screen the language has, minus the one about other applications. A standalone
    /// application is allowed to be as elaborate as a hosted one.</summary>
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

    // ---- reporting quality ----------------------------------------------------------------------

    /// <summary>Every refusal names where it is. A capability message without a JSON path is a
    /// puzzle: "this app uses a related-apps block" is not actionable in a 90 KB document.</summary>
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

    // ---- fixtures --------------------------------------------------------------------------------

    private static IReadOnlyList<Diagnostic> Validate(JsonObject definition) =>
        TargetValidator.Validate(definition, Standalone);

    /// <summary>The smallest thing the validator will walk. Not gate-clean and does not need to be —
    /// the gate has already run by the time a target is asked anything, so these fixtures exist to
    /// put ONE construct in front of the capability check without a 90 KB document around it.</summary>
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

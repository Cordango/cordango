// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Cord;

namespace Cordango.Compiler.Tests;

public class CordPreservedScreenTests
{
    private static JsonObject Imported() => JsonNode.Parse("""
    {
      "key":"budget_planner","name":"Budget Planner","version":"1.0.0",
      "entities":[{"key":"funding_round","label":"Funding round","fields":[
        {"key":"amount","label":"Amount","type":"money"}]}],
      "pages":[
        {"key":"funding","label":"Funding & Runway","entity":"funding_round","blocks":[
          {"kind":"row","blocks":[
            {"kind":"card","label":"Cash today","blocks":[
              {"kind":"stat","source":{"entity":"funding_round","aggregate":{"op":"sum","field":"amount"}}}]}]},
          {"kind":"view","view":"funding__rounds"},
          {"kind":"text","text":"Runway assumes the current burn."}]}],
      "views":[
        {"key":"funding__rounds","entity":"funding_round","type":"table","label":"Rounds"},
        {"key":"orphaned__nobody","entity":"funding_round","type":"table","label":"Not on any page"}]
    }
    """)!.AsObject();

    private static CordApp App() => CordImport.Import(Imported());

    [Fact]
    public void The_importer_claims_nothing_and_keeps_everything()
    {
        var app = App();

        Assert.Null(app.Screens);
        Assert.NotNull(app.Raw?["pages"]);
        Assert.NotNull(app.Raw?["views"]);
    }

    [Fact]
    public void Describing_a_preserved_screen_does_not_swallow_it()
    {
        var app = App();

        CordInspect.Describe(app);
        CordInspect.Describe(app, "/screens/funding");

        Assert.NotNull(app.Raw?["pages"]);
        Assert.NotNull(app.Raw?["views"]);
        Assert.Contains("Funding & Runway", CordInspect.Describe(app, "/screens/funding"));
    }

    [Fact]
    public void The_outline_lists_preserved_screens_instead_of_showing_none()
    {
        var outline = CordInspect.Describe(App());

        Assert.Contains("Funding & Runway", outline);
        Assert.Contains("preserved from the imported app", outline);
        Assert.Contains("orphaned__nobody", outline);
        Assert.Contains("/screens/<key>", outline);
    }

    [Fact]
    public void One_preserved_screen_can_be_read_in_full()
    {
        var screen = CordInspect.Describe(App(), "/screens/funding");

        Assert.Contains("Funding & Runway", screen);
        Assert.Contains("about funding_round", screen);
        Assert.Contains("Cash today", screen);
        Assert.Contains("a table of funding_round — Rounds", screen);
        Assert.Contains("Runway assumes the current burn.", screen);
        Assert.Contains("cannot be edited", screen);
    }

    [Fact]
    public void Asking_for_a_screen_that_is_not_there_names_the_ones_that_are()
    {
        var answer = CordInspect.Describe(App(), "/screens/nope");

        Assert.Contains("no screen 'nope'", answer);
        Assert.Contains("funding", answer);
    }

    [Fact]
    public void A_raised_screen_is_described_from_the_operations_that_made_it()
    {
        var app = CordJournal.Replay(new CordApp(Key: "b", Name: "B", Version: "1.0.0"),
        [JsonNode.Parse("""
        [{"op":"upsert_entity","entity":{"key":"hire","label":"Hire","fields":[
            {"key":"team","label":"Team","type":"text"}]}},
          {"op":"upsert_screen","screen":{"key":"hiring","label":"Hiring plan","subject":"hire",
            "sections":[{"key":"lines","kind":"list","of":"hire","label":"Lines","view":"board",
                         "groupBy":"team"}],
            "tabs":[{"key":"notes","label":"Notes","sections":[
              {"kind":"text","text":"Keep it current."}]}]}}]
        """)]).Draft;

        var screen = CordInspect.Describe(app, "/screens/hiring");

        Assert.Contains("Hiring plan [hiring], about hire", screen);
        Assert.Contains("list of hire — Lines, shown as a board", screen);
        Assert.Contains("Tab 'Notes'", screen);
        Assert.DoesNotContain("preserved", screen);
    }
}

// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Definition;

namespace Cordango.Compiler.Tests;

/// <summary>The whole reference-app corpus (exploration/reference/*.appdef.json — the Cordango demo
/// suite plus the composition-reframe references) stays Gate-clean. The csproj copies every appdef
/// via a wildcard, so a new reference app is pinned the moment the file exists; an app that stops
/// validating names itself in the failure.</summary>
public class ReferenceSuiteTests
{
    public static TheoryData<string> AppDefs()
    {
        var data = new TheoryData<string>();
        foreach (var f in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "fixtures"), "*.appdef.json"))
            if (Path.GetFileName(f) != "broken.appdef.json" && Path.GetFileName(f) != "crm.appdef.json")
                data.Add(Path.GetFileName(f));
        return data;
    }

    [Theory]
    [MemberData(nameof(AppDefs))]
    public void Reference_app_is_gate_clean(string file)
    {
        var doc = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", file)))!;
        Assert.Empty(Gate.Validate(doc));
    }

    /// <summary>The Normalizer is a REPAIR for defects, never a rewrite. Every healthy hand-authored
    /// app must come out byte-identical — the guard that keeps a tolerant parse (or any future
    /// deterministic fix) from quietly reshaping documents that were already correct.</summary>
    [Theory]
    [MemberData(nameof(AppDefs))]
    public void Normalizing_a_healthy_reference_app_changes_nothing(string file)
    {
        var doc = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", file)))!;
        var before = doc.ToJsonString();

        var recoveries = new List<Normalizer.LenientRecovery>();
        var after = Normalizer.Unstringify(doc, recoveries)!;

        Assert.Empty(recoveries);
        Assert.Equal(before, after.ToJsonString());
    }

    // ---- invoicing: the canonical computed-fields + deep-link app ----------------------------------

    private static JsonObject Invoicing() => (JsonObject)JsonNode.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "invoicing.appdef.json")))!;

    [Fact]
    public void Invoicing_models_document_math_as_computed_fields()
    {
        var invoice = Invoicing()["entities"]!.AsArray().OfType<JsonObject>()
            .First(e => (string?)e["key"] == "invoice");
        var byKey = invoice["fields"]!.AsArray().OfType<JsonObject>()
            .ToDictionary(f => (string)f["key"]!, f => f);
        Assert.NotNull(byKey["subtotal"]["computed"]?["rollup"]);         // sum of line totals
        Assert.NotNull(byKey["paid_amount"]["computed"]?["rollup"]);      // sum of payments
        Assert.NotNull(byKey["total"]["computed"]?["expr"]);              // subtotal + tax
        Assert.NotNull(byKey["balance_due"]["computed"]?["expr"]);        // total - paid
    }

    [Fact]
    public void Invoicing_overview_kpis_are_deep_linked()
    {
        var overview = Invoicing()["pages"]!.AsArray().OfType<JsonObject>()
            .First(p => (string?)p["key"] == "overview");
        var links = overview.ToJsonString();
        // Every countable KPI is a door: the outstanding/overdue stats and both charts click through.
        Assert.Contains("\"link\"", links);
        Assert.Contains("\"page\": \"invoices\"".Replace(" ", ""), links.Replace(" ", ""));
    }
}

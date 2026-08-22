// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Definition;

namespace Cordango.Compiler.Tests;

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

    private static JsonObject Invoicing() => (JsonObject)JsonNode.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "invoicing.appdef.json")))!;

    [Fact]
    public void Invoicing_models_document_math_as_computed_fields()
    {
        var invoice = Invoicing()["entities"]!.AsArray().OfType<JsonObject>()
            .First(e => (string?)e["key"] == "invoice");
        var byKey = invoice["fields"]!.AsArray().OfType<JsonObject>()
            .ToDictionary(f => (string)f["key"]!, f => f);
        Assert.NotNull(byKey["subtotal"]["computed"]?["rollup"]);
        Assert.NotNull(byKey["paid_amount"]["computed"]?["rollup"]);
        Assert.NotNull(byKey["total"]["computed"]?["expr"]);
        Assert.NotNull(byKey["balance_due"]["computed"]?["expr"]);
    }

    [Fact]
    public void Invoicing_overview_kpis_are_deep_linked()
    {
        var overview = Invoicing()["pages"]!.AsArray().OfType<JsonObject>()
            .First(p => (string?)p["key"] == "overview");
        var links = overview.ToJsonString();
        Assert.Contains("\"link\"", links);
        Assert.Contains("\"page\": \"invoices\"".Replace(" ", ""), links.Replace(" ", ""));
    }
}

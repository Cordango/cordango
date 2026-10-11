// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Cordango.Compile;
using Cordango.Definition;
using Cordango.SourceGen;
using Cordango.SourceGen.DotNet;

namespace Cordango.Standalone.Tests;

public class ScopedScreenTests
{
    [Fact]
    public void Nothing_the_screen_asks_for_is_reported_missing()
    {
        Assert.DoesNotContain(Build().Warnings, d => d.Code == NotYetCodes.BlockOption);
    }

    [Fact]
    public void The_split_and_the_repeat_hand_their_names_down()
    {
        var page = Flat(Receive());

        Assert.Matches("<SplitBlock[^>]* as=\"po\"", page);
        Assert.Contains("<template #detail=\"{ record, scopes }\">", page, StringComparison.Ordinal);
        Assert.Matches("<RepeatBlock[^>]* as=\"line\"[^>]* v-slot=\"\\{ record, scopes \\}\"", page);
    }

    [Fact]
    public void The_repeat_resolves_its_own_filters_against_the_screen_and_the_selection()
    {
        Assert.Matches("<RepeatBlock[^>]* :state=\"state\" :record=\"record\"", Flat(Receive()));
    }

    [Fact]
    public void A_condition_on_a_named_record_is_given_the_names()
    {
        Assert.Contains("visibleWhen({'value':'{{po.status}}','eq':'open'}, record, state, scopes)", Receive(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void An_action_that_names_its_record_carries_the_keys()
    {
        Assert.Contains(":keys=\"{'order':'{{po.id}}','item':'{{line.item}}'}\" :state=\"state\"", Receive(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_field_one_reference_away_is_emitted_as_its_path()
    {
        Assert.Contains("field=\"item.sku\"", Receive(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_card_that_opens_the_quick_look_owns_the_click()
    {
        var page = Flat(Receive());

        Assert.Contains(":open-detail=\"true\" entity=\"line\" :record=\"record\"", page, StringComparison.Ordinal);
        Assert.DoesNotMatch("<RepeatBlock[^>]* as=\"line\"[^>]*:clickable", page);
    }

    [Fact]
    public void A_sized_column_reaches_the_table()
    {
        Assert.Contains("'columns':[{'key':'amount','width':120},'quantity']", Receive(), StringComparison.Ordinal);
    }

    [Fact]
    public void Line_items_leave_out_the_parent_and_add_up_the_money()
    {
        var page = File("web/src/pages/OrderRecordPage.vue");

        Assert.Contains("'columns':['item','amount','quantity']", page, StringComparison.Ordinal);
        Assert.Contains(":totals=\"['amount']\"", page, StringComparison.Ordinal);
        Assert.Contains(":toolbar=\"false\"", page, StringComparison.Ordinal);
        Assert.Contains(":allow-delete=\"true\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reference_to_an_entity_of_this_application_names_it_for_a_filter()
    {
        var descriptors = File("api/Data/AppDescriptors.cs");

        Assert.Contains("(from, to) => to.Item = from.Item, \"part\")", descriptors, StringComparison.Ordinal);
        Assert.Contains("(from, to) => to.Amount = from.Amount)", descriptors, StringComparison.Ordinal);
    }

    [Fact]
    public void A_path_with_two_hops_is_still_reported()
    {
        var definition = Definition();
        var filters = definition["pages"]![0]!["blocks"]![0]!["blocks"]![0]!["source"]!["filters"]!.AsArray();
        filters.Add(new JsonObject { ["path"] = "item.barcode.length", ["operator"] = "eq", ["value"] = "1" });

        Assert.Contains(Build(definition).Warnings,
            d => d.Message.Contains("'item.barcode.length'", StringComparison.Ordinal));
    }

    private static string Receive() => File("web/src/pages/ReceivePage.vue");

    private static string Flat(string text) => Regex.Replace(text, "\\s+", " ");

    private static string File(string path) =>
        Build().Files.Single(f => f.RelativePath == path).Content;

    private static GenerateResult Build(JsonObject? definition = null)
    {
        var manifest = AppCompiler.Compile(definition ?? Definition(), "app1", At);
        return new DotNetVueGenerator().Generate(new GenerateRequest(
            new CompiledAppArtifact(manifest, manifest, "unhashed", new CompilerInfo("test", "1")),
            new JsonObject { ["allowIncomplete"] = true, ["seed"] = 42 }));
    }

    private static readonly DateTimeOffset At = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static JsonObject Definition() => (JsonObject)JsonNode.Parse("""
    {
      "schemaVersion":"2.0","key":"shop","name":"Shop","version":"1.0.0",
      "entities":[
        { "key":"part","label":"Part","labelPlural":"Parts","displayField":"name",
          "fields":[
            {"key":"name","label":"Name","type":"text"},
            {"key":"sku","label":"SKU","type":"text"},
            {"key":"barcode","label":"Barcode","type":"text"}
          ] },
        { "key":"order","label":"Order","labelPlural":"Orders","displayField":"number",
          "fields":[
            {"key":"number","label":"Number","type":"text"},
            {"key":"status","label":"Status","type":"select",
             "options":[{"value":"open","label":"Open"},{"value":"done","label":"Done"}]}
          ],
          "detail":{ "blocks":[
            { "kind":"child","entity":"line","via":"order","childType":"lineItems" }
          ] } },
        { "key":"line","label":"Line","labelPlural":"Lines","displayField":"item",
          "fields":[
            {"key":"order","label":"Order","type":"reference","targetEntity":"order"},
            {"key":"item","label":"Item","type":"reference","targetEntity":"part"},
            {"key":"amount","label":"Amount","type":"money"},
            {"key":"quantity","label":"Quantity","type":"integer"}
          ] }
      ],
      "commands":[
        { "key":"count","label":"Count it","entity":"line",
          "effects":[{"type":"notify","to":"{{record.created_by}}","title":"Counted","message":"Counted."}] }
      ],
      "pages":[
        { "key":"receive","label":"Receive",
          "state":[{"key":"scan","type":"text"}],
          "blocks":[
            { "kind":"split","as":"po","fields":["number"],
              "source":{"entity":"order"},
              "blocks":[
                { "kind":"repeat","as":"line",
                  "source":{"entity":"line","filters":[
                    {"field":"order","operator":"eq","value":"{{po.id}}"},
                    {"path":"item.barcode","operator":"eq","value":"{{state.scan}}","optional":true}
                  ]},
                  "blocks":[
                    { "kind":"card","openDetail":true,"blocks":[ {"kind":"field","field":"item.sku"} ] },
                    { "kind":"action","command":"count","entity":"line",
                      "keys":{"order":"{{po.id}}","item":"{{line.item}}"} }
                  ] },
                { "kind":"card","visibleWhen":{"value":"{{po.status}}","eq":"open"},
                  "blocks":[ {"kind":"text","value":"Still open."} ] }
              ] },
            { "kind":"table","source":{"entity":"line"},
              "fields":[ {"key":"amount","width":120}, "quantity" ] }
          ] }
      ]
    }
    """)!;
}

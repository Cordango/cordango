// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Compiler.Tests;

public class LegalEntityBlockGateTests
{
    private static JsonObject App(bool linkField = true)
    {
        var doc = (JsonObject)JsonNode.Parse("""
        {
          "schemaVersion": "2.0", "key": "accounts", "name": "Accounts", "version": "1.0.0",
          "entities": [
            { "key": "company", "label": "Company", "displayField": "name",
              "fields": [ { "key": "name", "label": "Name", "type": "text", "required": true } ] }
          ],
          "pages": [ { "key": "home", "label": "Home", "entity": "company", "blocks": [ { "kind": "text", "text": "Companies" } ] } ]
        }
        """)!;
        if (linkField)
            ((JsonArray)doc["entities"]![0]!["fields"]!).Add(
                JsonNode.Parse("""{ "key": "enr_legal_entity", "label": "Registry entry", "type": "text" }"""));
        return doc;
    }

    private static JsonObject WithDetailBlock(JsonObject doc, string block)
    {
        doc["entities"]![0]!["detail"] = new JsonObject { ["blocks"] = new JsonArray(JsonNode.Parse(block)) };
        return doc;
    }

    [Fact]
    public void A_record_detail_with_the_link_field_is_accepted()
    {
        Assert.Empty(Gate.Validate(WithDetailBlock(App(), """{ "kind": "legalEntity" }""")));
    }

    [Fact]
    public void A_record_detail_without_the_link_field_is_refused()
    {
        Assert.Contains(Gate.SemanticErrors(WithDetailBlock(App(linkField: false), """{ "kind": "legalEntity" }""")),
            e => e.Contains("'legalEntity' needs 'company' to carry the 'enr_legal_entity' link field"));
    }

    [Fact]
    public void A_page_is_refused()
    {
        var doc = App();
        doc["pages"]![0]!["blocks"] = new JsonArray(JsonNode.Parse("""{ "kind": "legalEntity" }"""));
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("'legalEntity' is only valid in a record detail"));
    }

    [Fact]
    public void An_unknown_property_is_refused()
    {
        Assert.NotEmpty(Gate.Validate(WithDetailBlock(App(), """{ "kind": "legalEntity", "entity": "x" }""")));
    }

    [Fact]
    public void It_is_not_offered_to_the_generator()
    {
        Assert.Contains("legalEntity", BlockKinds.NotAuthorable);
        Assert.DoesNotContain(BlockKinds.All, k => k.Canonical == "legalEntity");
    }
}

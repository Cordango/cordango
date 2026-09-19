// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Compiler.Tests;

public class DocumentsBlockGateTests
{
    private static JsonObject App() => (JsonObject)JsonNode.Parse("""
    {
      "schemaVersion": "2.0", "key": "delivery", "name": "Delivery", "version": "1.0.0",
      "uses": [ { "app": "core_documents", "entities": ["space"], "why": "A project owns one documentation space" } ],
      "entities": [
        { "key": "project", "label": "Project", "displayField": "name",
          "fields": [
            { "key": "name", "label": "Name", "type": "text", "required": true },
            { "key": "owner", "label": "Owner", "type": "reference", "targetApp": "platform", "targetEntity": "person" },
            { "key": "documentation", "label": "Documentation", "type": "reference",
              "targetApp": "core_documents", "targetEntity": "space", "onDelete": "setNull" } ] }
      ],
      "pages": [ { "key": "home", "label": "Home", "entity": "project", "blocks": [ { "kind": "documents" } ] } ]
    }
    """)!;

    private static JsonObject WithPageBlock(string block)
    {
        var doc = App();
        doc["pages"]![0]!["blocks"] = new JsonArray(JsonNode.Parse(block));
        return doc;
    }

    private static JsonObject WithDetailBlock(string block)
    {
        var doc = App();
        doc["entities"]![0]!["detail"] = new JsonObject { ["blocks"] = new JsonArray(JsonNode.Parse(block)) };
        return doc;
    }

    [Fact]
    public void A_documents_block_on_a_page_names_no_field()
    {
        Assert.Empty(Gate.Validate(WithPageBlock("""{ "kind": "documents" }""")));
    }

    [Fact]
    public void A_page_documents_block_with_a_field_is_refused()
    {
        Assert.Contains(Gate.SemanticErrors(WithPageBlock("""{ "kind": "documents", "field": "documentation" }""")),
            e => e.Contains("'documents' on a page takes no 'field'"));
    }

    [Fact]
    public void A_detail_documents_block_resolves_its_space_reference()
    {
        Assert.Empty(Gate.Validate(WithDetailBlock("""{ "kind": "documents", "field": "documentation" }""")));
    }

    [Fact]
    public void A_detail_documents_block_needs_a_field()
    {
        Assert.Contains(Gate.SemanticErrors(WithDetailBlock("""{ "kind": "documents" }""")),
            e => e.Contains("'documents' in a record detail needs a 'field'"));
    }

    [Fact]
    public void The_field_must_exist_on_the_record()
    {
        Assert.Contains(Gate.SemanticErrors(WithDetailBlock("""{ "kind": "documents", "field": "handbook" }""")),
            e => e.Contains("documents field 'handbook' is not a field of 'project'"));
    }

    [Fact]
    public void The_field_must_be_a_reference_to_a_documents_space()
    {
        Assert.Contains(Gate.SemanticErrors(WithDetailBlock("""{ "kind": "documents", "field": "owner" }""")),
            e => e.Contains("documents field 'project.owner' must be a reference with targetApp 'core_documents'"));
    }

    [Fact]
    public void A_documents_block_inside_a_repeat_is_refused()
    {
        var doc = WithPageBlock("""{ "kind": "repeat", "source": { "entity": "project" }, "blocks": [ { "kind": "documents" } ] }""");
        Assert.Contains(Gate.SemanticErrors(doc), e => e.Contains("not inside a repeat"));
    }
}

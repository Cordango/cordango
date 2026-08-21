// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Compiler.Tests;

/// <summary>
/// The two field roles that put a record on the public internet.
///
/// <para><c>publicShare</c> is the switch and <c>publicToken</c> is the address, and between them
/// they are the whole of "this record is served to people with no account". Everything the perimeter
/// does downstream — resolving a link, refusing an unpublished one — reads these two fields, so a
/// definition that declares them wrongly is a definition that would either serve something it should
/// not or serve nothing at all.</para>
///
/// <para>Which is why these are checked at AUTHORING time. A token that a person can type is the one
/// mistake with no visible symptom: the page works perfectly, and the only thing wrong with it is
/// that somebody else can guess the address. There is no runtime moment at which that fails.</para>
/// </summary>
public class PublicShareGateTests
{
    /// <summary>A minimal app with one publishable entity, declared correctly.</summary>
    private static JsonObject ShareApp() => (JsonObject)JsonNode.Parse("""
    {
      "schemaVersion": "2.0", "key": "pub", "name": "Pub", "version": "1.0.0",
      "entities": [
        { "key": "page", "label": "Page", "displayField": "name",
          "fields": [
            { "key": "name", "label": "Name", "type": "text" },
            { "key": "is_published", "label": "Published", "type": "boolean", "role": "publicShare" },
            { "key": "public_token", "label": "Link", "type": "text", "role": "publicToken",
              "unique": true } ] }
      ],
      "views": [{ "key": "p", "label": "Pages", "type": "table", "entity": "page" }],
      "roles": [{ "key": "admin", "name": "Admin",
                  "grants": [{ "entity": "*", "create": true, "read": true, "update": true, "delete": true }] }]
    }
    """)!;

    private static JsonObject Entity(JsonObject doc, string key) =>
        ((JsonArray)doc["entities"]!).OfType<JsonObject>().First(e => (string?)e["key"] == key);

    private static JsonArray Fields(JsonObject doc, string entity) => (JsonArray)Entity(doc, entity)["fields"]!;

    private static JsonObject Field(JsonObject doc, string entity, string field) =>
        Fields(doc, entity).OfType<JsonObject>().First(f => (string?)f["key"] == field);

    private static void Drop(JsonObject doc, string entity, string field)
    {
        var fields = Fields(doc, entity);
        var found = fields.OfType<JsonObject>().First(f => (string?)f["key"] == field);
        fields.Remove(found);
    }

    [Fact]
    public void A_correctly_declared_publishable_entity_passes() => Assert.Empty(Gate.Validate(ShareApp()));

    [Fact]
    public void Publishing_is_on_or_off_so_the_share_flag_must_be_a_boolean()
    {
        var doc = ShareApp();
        Field(doc, "page", "is_published")["type"] = "text";
        Assert.Contains(Gate.SemanticErrors(doc), e =>
            e.Contains("'page.is_published' has role 'publicShare'") && e.Contains("must be a 'boolean' field"));
    }

    /// <summary>
    /// A computed share flag would mean the answer to "is this on the internet" is recalculated by a
    /// formula on every write. That is a decision, not a derivation.
    /// </summary>
    [Fact]
    public void The_share_flag_cannot_be_computed()
    {
        var doc = ShareApp();
        Field(doc, "page", "is_published")["computed"] = JsonNode.Parse("""{ "expr": "true" }""");
        Assert.Contains(Gate.SemanticErrors(doc), e =>
            e.Contains("'page.is_published' has role 'publicShare' and is computed"));
    }

    [Fact]
    public void The_token_holds_an_opaque_address_so_it_must_be_text()
    {
        var doc = ShareApp();
        Field(doc, "page", "public_token")["type"] = "integer";
        Assert.Contains(Gate.SemanticErrors(doc), e =>
            e.Contains("'page.public_token' has role 'publicToken'") && e.Contains("must be a 'text' field"));
    }

    /// <summary>Two records at one address is a link that resolves to whichever was written first.</summary>
    [Fact]
    public void The_token_must_be_unique()
    {
        var doc = ShareApp();
        Field(doc, "page", "public_token")["unique"] = false;
        Assert.Contains(Gate.SemanticErrors(doc), e =>
            e.Contains("'page.public_token' has role 'publicToken' but is not unique"));
    }

    /// <summary>
    /// The token is GENERATED, and the author does not get a say. They cannot declare it read-only —
    /// that is not an authorable property — so the compiler stamps the role instead, which is also
    /// why nothing here asks them to. What they CAN do is declare a default, and that is the one way
    /// to hand themselves a guessable address: it would either be silently overwritten or, worse,
    /// honoured. Refused rather than ignored, because the working page would never say which.
    /// </summary>
    [Fact]
    public void The_token_cannot_be_given_a_default()
    {
        var doc = ShareApp();
        Field(doc, "page", "public_token")["default"] = "intro-call";
        Assert.Contains(Gate.SemanticErrors(doc), e =>
            e.Contains("'page.public_token' has role 'publicToken' and declares a default"));
    }

    [Fact]
    public void An_entity_may_be_published_only_one_way()
    {
        var doc = ShareApp();
        Fields(doc, "page").Add(JsonNode.Parse("""
            { "key": "also_published", "label": "Also", "type": "boolean", "role": "publicShare" }
            """));
        Assert.Contains(Gate.SemanticErrors(doc), e =>
            e.Contains("multiple role:'publicShare' fields"));
    }

    [Fact]
    public void An_entity_may_be_published_at_only_one_address()
    {
        var doc = ShareApp();
        Fields(doc, "page").Add(JsonNode.Parse("""
            { "key": "other_token", "label": "Other", "type": "text", "role": "publicToken",
              "unique": true }
            """));
        Assert.Contains(Gate.SemanticErrors(doc), e =>
            e.Contains("multiple role:'publicToken' fields"));
    }

    // --- the pair -------------------------------------------------------------------------------
    //
    // Both directions, because either half alone is reachable by deleting a field and not noticing
    // what it was holding up.

    [Fact]
    public void A_switch_with_no_address_has_nowhere_to_publish_to()
    {
        var doc = ShareApp();
        Drop(doc, "page", "public_token");
        Assert.Contains(Gate.SemanticErrors(doc), e =>
            e.Contains("has a role:'publicShare' field") && e.Contains("no role:'publicToken' field"));
    }

    [Fact]
    public void An_address_with_no_switch_is_one_nobody_can_turn_off()
    {
        var doc = ShareApp();
        Drop(doc, "page", "is_published");
        Assert.Contains(Gate.SemanticErrors(doc), e =>
            e.Contains("has a role:'publicToken' field") && e.Contains("no role:'publicShare' field"));
    }

    /// <summary>An app that publishes nothing declares neither, and that is the ordinary case.</summary>
    [Fact]
    public void An_entity_that_publishes_nothing_needs_neither()
    {
        var doc = ShareApp();
        Drop(doc, "page", "is_published");
        Drop(doc, "page", "public_token");
        Assert.Empty(Gate.Validate(doc));
    }
}

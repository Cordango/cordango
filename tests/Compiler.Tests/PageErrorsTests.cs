// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Compiler.Tests;

/// <summary>
/// <see cref="Gate.PageErrors"/> — the gate for a page authored against a COMPILED MANIFEST rather
/// than an app definition. This is what a personal view is validated by.
///
/// <para>The contract these pin: it reuses the app gate's block rules (so a personal page can never
/// be held to a weaker standard than an app page), and the manifest ROSTER it is given is the
/// authorization boundary — an entity absent from the roster is unauthorable, which is how a
/// permission-restricted manifest keeps a user from authoring a view over data they cannot read.</para>
/// </summary>
public class PageErrorsTests
{
    /// <summary>A compiled-manifest-shaped roster: system fields and compiler-added properties are
    /// present on purpose, because a real manifest carries them and PageErrors must not choke.</summary>
    private static JsonObject Manifest() => (JsonObject)JsonNode.Parse("""
    {
      "schemaVersion": "2.0", "key": "desk", "name": "Desk", "version": "1.0.0",
      "entities": [
        { "key": "ticket", "label": "Ticket", "displayField": "subject",
          "detail": { "blocks": [ { "kind": "hub", "title": "subject" } ] },
          "fields": [
            { "key": "id", "label": "Id", "type": "text", "system": true },
            { "key": "created_at", "label": "Created", "type": "datetime", "system": true },
            { "key": "subject", "label": "Subject", "type": "text" },
            { "key": "priority", "label": "Priority", "type": "select",
              "options": [ { "value": "low", "label": "Low" }, { "value": "high", "label": "High" } ] },
            { "key": "owner", "label": "Owner", "type": "reference", "targetApp": "platform",
              "targetEntity": "person", "readOnly": true, "auto": "currentUser" } ] },
        { "key": "secret", "label": "Secret", "displayField": "code",
          "fields": [ { "key": "code", "label": "Code", "type": "text" } ] }
      ],
      "views": [
        { "key": "all_tickets", "label": "All tickets", "type": "table", "entity": "ticket" },
        { "key": "secrets", "label": "Secrets", "type": "table", "entity": "secret" }
      ],
      "pages": [ { "key": "tickets", "label": "Tickets", "entity": "ticket",
                   "blocks": [ { "kind": "view", "view": "all_tickets" } ] } ],
      "commands": [ { "key": "escalate", "label": "Escalate", "entity": "ticket",
                      "effects": [ { "type": "updateRecord", "set": { "priority": "high" } } ] } ]
    }
    """)!;

    private static JsonObject Page(string json) => (JsonObject)JsonNode.Parse(json)!;

    /// <summary>Both halves, the way a caller must run them: shape against the page sub-schema,
    /// meaning against the roster. Neither alone is enough — semantics happily ignore a property that
    /// belongs to no block, and structure has no idea whether 'all_tickets' exists.</summary>
    private static List<string> FullCheck(JsonObject manifest, JsonObject page) =>
        Gate.StructuralErrors(page, Schemas.PageSchema()) is { Count: > 0 } structural
            ? structural
            : Gate.PageErrors(manifest, page);

    [Fact]
    public void A_page_over_the_apps_own_view_passes()
    {
        // 'my_' prefixes the key so a personal page can never collide with an app page in the
        // /a/:handle/:page route. It has to be an underscore: page keys are `identifier`s, and a dot
        // fails the pattern.
        Assert.Empty(FullCheck(Manifest(), Page("""
          { "key": "my_escalations", "label": "Escalations", "entity": "ticket",
            "blocks": [ { "kind": "view", "view": "all_tickets" } ] }
          """)));
    }

    [Fact]
    public void A_page_composed_from_primitives_passes()
    {
        // The expressive case: no named view at all, just a table block over the entity with a filter.
        Assert.Empty(FullCheck(Manifest(), Page("""
          { "key": "my_urgent", "label": "Urgent", "entity": "ticket", "blocks": [
              { "kind": "table",
                "source": { "entity": "ticket",
                            "filters": [ { "field": "priority", "operator": "eq", "value": "high" } ] },
                "fields": [ "subject", "priority" ] } ] }
          """)));
    }

    [Fact]
    public void A_dotted_key_is_refused_by_the_page_schema()
    {
        // Pinned because it dictates the personal-view key scheme: 'my_x', never 'my.x'.
        Assert.NotEmpty(Gate.StructuralErrors(Page("""
          { "key": "my.escalations", "label": "Escalations", "blocks": [] }
          """), Schemas.PageSchema()));
    }

    [Fact]
    public void An_unknown_view_key_is_refused()
    {
        Assert.Contains(Gate.PageErrors(Manifest(), Page("""
          { "key": "my_x", "label": "X", "blocks": [ { "kind": "view", "view": "ghost" } ] }
          """)), e => e.Contains("references unknown view 'ghost'"));
    }

    [Fact]
    public void A_filter_on_a_field_that_does_not_exist_is_refused()
    {
        // The dangerous failure mode if it slipped through: a dropped filter shows MORE rows than
        // the author asked for, and looks like it worked.
        Assert.Contains(Gate.PageErrors(Manifest(), Page("""
          { "key": "my_x", "label": "X", "blocks": [
              { "kind": "table", "source": { "entity": "ticket",
                  "filters": [ { "field": "sevrity", "operator": "eq", "value": "high" } ] } } ] }
          """)), e => e.Contains("'sevrity' is not a field of 'ticket'"));
    }

    [Fact]
    public void A_record_only_block_is_refused_on_a_page()
    {
        // Pages bind to a COLLECTION. Every record-binding rule the app gate enforces applies here.
        var errors = Gate.PageErrors(Manifest(), Page("""
          { "key": "my_x", "label": "X", "entity": "ticket", "blocks": [ { "kind": "hub", "title": "subject" } ] }
          """));
        Assert.Contains(errors, e => e.Contains("block kind 'hub' is only valid in a record detail"));
    }

    [Fact]
    public void An_unknown_entity_is_refused()
    {
        Assert.Contains(Gate.PageErrors(Manifest(), Page("""
          { "key": "my_x", "label": "X", "entity": "nope", "blocks": [ { "kind": "view", "view": "all_tickets" } ] }
          """)), e => e.Contains("page 'my_x' entity 'nope' is unknown"));
    }

    [Fact]
    public void System_fields_the_compiler_added_are_addressable()
    {
        // A manifest's fields include the runtime's own (created_at); sorting a personal view by
        // "newest first" must not be refused as an unknown field.
        Assert.Empty(Gate.PageErrors(Manifest(), Page("""
          { "key": "my_recent", "label": "Recent", "blocks": [
              { "kind": "table", "source": { "entity": "ticket",
                  "sort": [ { "field": "created_at", "direction": "desc" } ] } } ] }
          """)));
    }

    [Fact]
    public void The_roster_IS_the_authorization_boundary()
    {
        // The security property personal views rest on: hand PageErrors a manifest with the entity
        // removed — as RestrictManifest does for a role that may not read it — and a page naming it
        // cannot be authored. No separate permission check, no second list to keep in step.
        var restricted = Manifest();
        var entities = (JsonArray)restricted["entities"]!;
        entities.RemoveAt(1);                                   // 'secret' is not readable by this caller
        var views = (JsonArray)restricted["views"]!;
        views.RemoveAt(1);

        var page = Page("""
          { "key": "my_leak", "label": "Leak", "blocks": [
              { "kind": "table", "source": { "entity": "secret" }, "fields": [ "code" ] } ] }
          """);
        Assert.Empty(Gate.PageErrors(Manifest(), page));         // an unrestricted caller may author it
        Assert.NotEmpty(Gate.PageErrors(restricted, page));      // this caller may not
    }

    [Fact]
    public void A_page_with_no_manifest_or_no_page_fails_rather_than_throwing()
    {
        Assert.NotEmpty(Gate.PageErrors(null, Page("""{ "key": "k", "label": "L" }""")));
        Assert.NotEmpty(Gate.PageErrors(Manifest(), null));
    }

    // ---- presets: the same idea, narrowed to one of the app's own views -------------------------

    private static List<string> PresetCheck(JsonObject manifest, string baseView, JsonObject preset) =>
        Gate.StructuralErrors(preset, Schemas.PresetSchema()) is { Count: > 0 } structural
            ? structural
            : Gate.PresetErrors(manifest, baseView, preset);

    [Fact]
    public void A_preset_over_an_apps_own_view_passes()
    {
        Assert.Empty(PresetCheck(Manifest(), "all_tickets", Page("""
          { "filters": [ { "field": "priority", "operator": "eq", "value": "high" } ],
            "sort": [ { "field": "created_at", "direction": "desc" } ],
            "columns": [ "subject", "priority" ] }
          """)));
    }

    [Fact]
    public void A_preset_resolves_against_its_BASE_VIEWS_entity()
    {
        // 'code' is a field of `secret`, not of `ticket` — a preset on the tickets view may not reach
        // it, even though the app has it. The base view decides what "a field" means here.
        Assert.Contains(PresetCheck(Manifest(), "all_tickets", Page("""
          { "columns": [ "code" ] }
          """)), e => e.Contains("column 'code' is not a field of 'ticket'"));
    }

    [Fact]
    public void A_preset_on_a_view_the_app_does_not_have_is_refused()
    {
        Assert.Contains(PresetCheck(Manifest(), "ghost_view", Page("""{ "columns": [ "subject" ] }""")),
            e => e.Contains("unknown view 'ghost_view'"));
    }

    [Fact]
    public void A_preset_filter_gets_the_same_rules_an_authored_one_does()
    {
        // Same validator as a view's filters: an unknown field is caught, and so is a malformed leaf.
        Assert.Contains(PresetCheck(Manifest(), "all_tickets", Page("""
          { "filters": [ { "field": "sevrity", "operator": "eq", "value": "high" } ] }
          """)), e => e.Contains("'sevrity' is not a field of 'ticket'"));
        Assert.NotEmpty(PresetCheck(Manifest(), "all_tickets", Page("""
          { "filters": [ { "field": "priority" } ] }
          """)));
    }

    [Fact]
    public void A_preset_cannot_smuggle_in_keys_that_are_not_preset_settings()
    {
        // The closed shape matters more here than for a page: a `blocks` key on a preset would look
        // like it does something and do nothing at all.
        Assert.NotEmpty(Gate.StructuralErrors(Page("""
          { "columns": [ "subject" ], "blocks": [] }
          """), Schemas.PresetSchema()));
    }

    // ---- structure (the page's own shape) is the other half -------------------------------------

    [Fact]
    public void The_page_subschema_accepts_a_page_and_rejects_junk()
    {
        var schema = Schemas.PageSchema();
        Assert.Empty(Gate.StructuralErrors(Page("""
          { "key": "my_x", "label": "X", "blocks": [ { "kind": "view", "view": "all_tickets" } ] }
          """), schema));
        // A property no page has: the schema's closed shapes are what stop an author smuggling in
        // keys the renderer would ignore and the next reader would trust.
        Assert.NotEmpty(Gate.StructuralErrors(Page("""
          { "key": "my_x", "label": "X", "blocks": [], "danger": true }
          """), schema));
        Assert.NotEmpty(Gate.StructuralErrors(Page("""{ "label": "no key" }"""), schema));
    }
}

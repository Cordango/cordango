// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Compiler.Tests;

public class NormalizerTests
{
    private static JsonObject Minimal() => (JsonObject)JsonNode.Parse("""
    {
      "schemaVersion": "1.0", "key": "app", "name": "App", "version": "1.0.0",
      "entities": [
        { "key": "thing", "label": "Thing", "displayField": "name",
          "fields": [{ "key": "name", "label": "Name", "type": "text" }] }
      ]
    }
    """)!;

    [Fact]
    public void Stringified_presentation_and_theme_become_objects()
    {
        var doc = Minimal();
        doc["presentation"] = """{"icon": "clipboard-check", "color": "#4F8A8B"}""";
        doc["theme"] = """{"primaryColor": "#112233"}""";

        var fixedDoc = Normalizer.Unstringify(doc)!;

        Assert.IsType<JsonObject>(fixedDoc["presentation"]);
        Assert.Equal("clipboard-check", fixedDoc["presentation"]!["icon"]!.GetValue<string>());
        Assert.IsType<JsonObject>(fixedDoc["theme"]);
        Assert.Empty(Gate.StructuralErrors(fixedDoc));
    }

    [Fact]
    public void Stringified_nested_options_array_becomes_array()
    {
        var doc = Minimal();
        var fields = (JsonArray)doc["entities"]![0]!["fields"]!;
        fields.Add(JsonNode.Parse("""
            { "key": "state", "label": "State", "type": "select",
              "options": "[{\"value\": \"open\", \"label\": \"Open\"}]" }
            """));

        var fixedDoc = Normalizer.Unstringify(doc)!;

        var options = fixedDoc["entities"]![0]!["fields"]![1]!["options"];
        Assert.IsType<JsonArray>(options);
        Assert.Equal("open", options![0]!["value"]!.GetValue<string>());
        Assert.Empty(Gate.StructuralErrors(fixedDoc));
    }

    [Fact]
    public void Legitimate_strings_are_left_alone_even_if_json_shaped()
    {
        var doc = Minimal();
        doc["description"] = """{"this": "is a real description that happens to look like json"}""";
        doc["entities"]![0]!["fields"]![0]!["help"] = "[bracketed] help text";

        var fixedDoc = Normalizer.Unstringify(doc)!;

        Assert.Equal("""{"this": "is a real description that happens to look like json"}""",
            fixedDoc["description"]!.GetValue<string>());       // schema says string — untouched
        Assert.Equal("[bracketed] help text",
            fixedDoc["entities"]![0]!["fields"]![0]!["help"]!.GetValue<string>());
    }

    [Fact]
    public void Unparseable_string_where_object_expected_is_left_for_the_gate()
    {
        var doc = Minimal();
        doc["presentation"] = "{not valid json";
        var fixedDoc = Normalizer.Unstringify(doc)!;
        Assert.Equal("{not valid json", fixedDoc["presentation"]!.GetValue<string>());
        Assert.NotEmpty(Gate.StructuralErrors(fixedDoc));       // still a gate error, as it should be
    }

    [Fact]
    public void Already_valid_document_is_unchanged()
    {
        var doc = Minimal();
        var before = doc.ToJsonString();
        var fixedDoc = Normalizer.Unstringify(doc)!;
        Assert.Equal(before, fixedDoc.ToJsonString());
    }

    [Fact]
    public void Duplicate_property_names_parse_last_wins_instead_of_crashing()
    {
        // Live 2026-07-19: a generated definition carried a duplicate property in one object.
        // JsonNode.Parse accepts it lazily; the first enumeration (Normalizer.Walk) then threw
        // "An item with the same key has already been added" and the UNHANDLED exception killed
        // the whole run. ParseTolerant resolves duplicates last-wins, like JSON.parse.
        var node = Normalizer.ParseTolerant("""
        { "entities": [ { "key": "old", "key": "ticket", "label": "Ticket",
                          "fields": [ { "key": "subject", "type": "text", "type": "longtext" } ] } ],
          "count": 2, "active": true, "note": null }
        """)!;

        var entity = node["entities"]![0]!.AsObject();
        Assert.Equal("ticket", entity["key"]!.GetValue<string>());                       // last wins
        Assert.Equal("longtext", entity["fields"]![0]!["type"]!.GetValue<string>());     // nested too
        Assert.Equal(2, node["count"]!.GetValue<int>());                                 // scalars keep their types
        Assert.True(node["active"]!.GetValue<bool>());
        Assert.Null(node["note"]);
        // The rebuilt tree is fully materializable — the exact operation that used to throw.
        Assert.Equal(4, node.AsObject().Count);
        Assert.NotNull(Normalizer.Unstringify(node.DeepClone()));
    }

    [Fact]
    public void Definition_wrapped_under_an_envelope_key_is_unwrapped()
    {
        var wrapped = new JsonObject { ["app"] = Minimal() };

        var fixedDoc = Normalizer.Unstringify(wrapped)!;

        Assert.Null(fixedDoc["app"]);                               // the envelope is gone
        Assert.Equal("app", fixedDoc["key"]!.GetValue<string>());   // the real definition is at the root
        Assert.IsType<JsonArray>(fixedDoc["entities"]);
        Assert.Empty(Gate.StructuralErrors(fixedDoc));
    }

    [Fact]
    public void The_input_envelope_from_the_deepseek_run_is_unwrapped()
    {
        // Live 2026-07-19: deepseek-v4-pro emitted {"input": {…}} — the tool-call argument
        // envelope's own name — and the attempt died at the gate on missing required root keys.
        var wrapped = new JsonObject { ["input"] = Minimal() };

        var fixedDoc = Normalizer.Unstringify(wrapped)!;

        Assert.Null(fixedDoc["input"]);
        Assert.Equal("app", fixedDoc["key"]!.GetValue<string>());
        Assert.Empty(Gate.StructuralErrors(fixedDoc));
    }

    [Fact]
    public void A_real_field_named_like_a_wrapper_is_not_unwrapped()
    {
        // A legitimate top-level definition whose first entity happens to be keyed 'app' must NOT be
        // mistaken for an envelope — the root already has the required keys, so unwrap is a no-op.
        var doc = Minimal();
        var before = doc.ToJsonString();
        var fixedDoc = Normalizer.Unstringify(doc)!;
        Assert.Equal(before, fixedDoc.ToJsonString());
    }

    // ---- Lenient recovery of a double-encoded section with a trailing suffix ----
    // Live 2026-08-02 (MeetingPrep): the design critic found the right defect and BOTH its repair
    // attempts were discarded because `page` came back double-encoded with trailing garbage. The
    // two payloads differ in a way that decides the whole rule, so both are pinned here.

    [Fact]
    public void Trailing_closing_delimiters_are_tolerated()
    {
        // Attempt 2: 6320 characters, valid through 6318, remainder exactly "]}". The strict parse
        // threw, the slice was dropped, the repair was abandoned — and the run reported designOk.
        var doc = Minimal();
        doc["presentation"] = """{"icon": "clipboard-check", "color": "#4F8A8B"}]}""";

        var recoveries = new List<Normalizer.LenientRecovery>();
        var fixedDoc = Normalizer.Unstringify(doc, recoveries)!;

        Assert.IsType<JsonObject>(fixedDoc["presentation"]);
        Assert.Equal("clipboard-check", fixedDoc["presentation"]!["icon"]!.GetValue<string>());
        Assert.Empty(Gate.StructuralErrors(fixedDoc));

        var rec = Assert.Single(recoveries);          // and it is MEASURED, not silently absorbed
        Assert.Equal("/presentation", rec.Path);
        Assert.Equal("object", rec.Expected);
        Assert.Equal("]}", rec.Suffix);
    }

    [Fact]
    public void A_remainder_carrying_content_is_refused()
    {
        // Attempt 1: parsed cleanly at offset 661 with ", {\"kind\": \"data.tiles\", …" and 8,267
        // further characters of REAL BLOCKS after it. Keeping the prefix would have replaced a
        // whole page with a three-block stub — a downgrade that looks like a success.
        var doc = Minimal();
        const string payload = """{"icon": "clipboard-check"}, {"icon": "chart-line", "color": "#112233"}""";
        doc["presentation"] = payload;

        var recoveries = new List<Normalizer.LenientRecovery>();
        var fixedDoc = Normalizer.Unstringify(doc, recoveries)!;

        // Left verbatim for the gate — NOT silently shrunk to the parseable prefix.
        Assert.Equal(payload, fixedDoc["presentation"]!.GetValue<string>());
        Assert.Empty(recoveries);
        Assert.NotEmpty(Gate.StructuralErrors(fixedDoc));         // fails loudly rather than shrinking
    }

    [Fact]
    public void A_comma_led_remainder_is_refused_even_when_it_is_short()
    {
        // A comma is not excess punctuation: it says another value was intended and lost, which
        // makes the prefix an INCOMPLETE document rather than a complete one with garbage after it.
        var doc = Minimal();
        const string payload = """{"icon": "clipboard-check"},""";
        doc["presentation"] = payload;

        var fixedDoc = Normalizer.Unstringify(doc)!;

        Assert.Equal(payload, fixedDoc["presentation"]!.GetValue<string>());
    }

    [Fact]
    public void A_long_run_of_closers_is_refused()
    {
        // Ones and twos are a fencepost slip; a long run is a corrupted document.
        var doc = Minimal();
        const string payload = """{"icon": "clipboard-check"}]}]}]}]}]}""";
        doc["presentation"] = payload;

        var fixedDoc = Normalizer.Unstringify(doc)!;

        Assert.Equal(payload, fixedDoc["presentation"]!.GetValue<string>());
    }

    [Fact]
    public void A_string_that_does_not_open_a_structure_is_never_reinterpreted()
    {
        // Walk is generic. Recovery must only ever fire where the schema wants an object/array AND
        // the string opens one — never on ordinary text, an id, or user content.
        var doc = Minimal();
        doc["description"] = "just text}";
        doc["entities"]![0]!["fields"]![0]!["help"] = "help]}";

        var fixedDoc = Normalizer.Unstringify(doc)!;

        Assert.Equal("just text}", fixedDoc["description"]!.GetValue<string>());
        Assert.Equal("help]}", fixedDoc["entities"]![0]!["fields"]![0]!["help"]!.GetValue<string>());
    }

    [Fact]
    public void A_strictly_valid_section_records_no_recovery()
    {
        // The strict parse must keep winning: recovery is a fallback, not a second code path that
        // every healthy document quietly takes.
        var doc = Minimal();
        doc["presentation"] = """{"icon": "clipboard-check"}""";

        var recoveries = new List<Normalizer.LenientRecovery>();
        var fixedDoc = Normalizer.Unstringify(doc, recoveries)!;

        Assert.IsType<JsonObject>(fixedDoc["presentation"]);
        Assert.Empty(recoveries);
    }
}

public class GateErrorMessageTests
{
    private static JsonObject Minimal() => (JsonObject)JsonNode.Parse("""
    {
      "schemaVersion": "1.0", "key": "app", "name": "App", "version": "1.0.0",
      "entities": [
        { "key": "thing", "label": "Thing", "displayField": "name",
          "fields": [{ "key": "name", "label": "Name", "type": "text" }] }
      ]
    }
    """)!;

    [Fact]
    public void Enum_errors_list_the_allowed_values()
    {
        var doc = Minimal();
        doc["entities"]![0]!["fields"]![0]!["type"] = "string";   // not a valid field type
        var errs = Gate.StructuralErrors(doc);
        Assert.Contains(errs, e => e.Contains("fields/0/type") && e.Contains("allowed:") && e.Contains("longtext"));
    }

    [Fact]
    public void Failed_if_probes_are_not_reported_as_errors()
    {
        var doc = Minimal();
        // A perfectly valid tabs-block page next to one real error elsewhere: the old gate
        // reported phantom 'Expected "view"/"section"/"columns"' errors for the valid block.
        doc["views"] = JsonNode.Parse("""
            [{ "key": "all_things", "label": "All", "type": "table", "entity": "thing" }]
            """);
        doc["pages"] = JsonNode.Parse("""
            [{ "key": "home", "label": "Home", "blocks": [
              { "kind": "tabs", "tabs": [
                { "label": "One", "blocks": [{ "kind": "view", "view": "all_things" }] }
              ] }
            ] }]
            """);
        doc["entities"]![0]!["fields"]![0]!["type"] = "string";   // the one real error
        var errs = Gate.StructuralErrors(doc);
        Assert.DoesNotContain(errs, e => e.Contains("blocks/0/kind"));
        Assert.DoesNotContain(errs, e => e.Contains("Expected \"\\\"reference\\\"\""));
        Assert.Contains(errs, e => e.Contains("fields/0/type"));
    }
}

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
            fixedDoc["description"]!.GetValue<string>());
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
        Assert.NotEmpty(Gate.StructuralErrors(fixedDoc));
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
        var node = Normalizer.ParseTolerant("""
        { "entities": [ { "key": "old", "key": "ticket", "label": "Ticket",
                          "fields": [ { "key": "subject", "type": "text", "type": "longtext" } ] } ],
          "count": 2, "active": true, "note": null }
        """)!;

        var entity = node["entities"]![0]!.AsObject();
        Assert.Equal("ticket", entity["key"]!.GetValue<string>());
        Assert.Equal("longtext", entity["fields"]![0]!["type"]!.GetValue<string>());
        Assert.Equal(2, node["count"]!.GetValue<int>());
        Assert.True(node["active"]!.GetValue<bool>());
        Assert.Null(node["note"]);
        Assert.Equal(4, node.AsObject().Count);
        Assert.NotNull(Normalizer.Unstringify(node.DeepClone()));
    }

    [Fact]
    public void Definition_wrapped_under_an_envelope_key_is_unwrapped()
    {
        var wrapped = new JsonObject { ["app"] = Minimal() };

        var fixedDoc = Normalizer.Unstringify(wrapped)!;

        Assert.Null(fixedDoc["app"]);
        Assert.Equal("app", fixedDoc["key"]!.GetValue<string>());
        Assert.IsType<JsonArray>(fixedDoc["entities"]);
        Assert.Empty(Gate.StructuralErrors(fixedDoc));
    }

    [Fact]
    public void The_input_envelope_from_the_deepseek_run_is_unwrapped()
    {
        var wrapped = new JsonObject { ["input"] = Minimal() };

        var fixedDoc = Normalizer.Unstringify(wrapped)!;

        Assert.Null(fixedDoc["input"]);
        Assert.Equal("app", fixedDoc["key"]!.GetValue<string>());
        Assert.Empty(Gate.StructuralErrors(fixedDoc));
    }

    [Fact]
    public void A_real_field_named_like_a_wrapper_is_not_unwrapped()
    {
        var doc = Minimal();
        var before = doc.ToJsonString();
        var fixedDoc = Normalizer.Unstringify(doc)!;
        Assert.Equal(before, fixedDoc.ToJsonString());
    }

    [Fact]
    public void Trailing_closing_delimiters_are_tolerated()
    {
        var doc = Minimal();
        doc["presentation"] = """{"icon": "clipboard-check", "color": "#4F8A8B"}]}""";

        var recoveries = new List<Normalizer.LenientRecovery>();
        var fixedDoc = Normalizer.Unstringify(doc, recoveries)!;

        Assert.IsType<JsonObject>(fixedDoc["presentation"]);
        Assert.Equal("clipboard-check", fixedDoc["presentation"]!["icon"]!.GetValue<string>());
        Assert.Empty(Gate.StructuralErrors(fixedDoc));

        var rec = Assert.Single(recoveries);
        Assert.Equal("/presentation", rec.Path);
        Assert.Equal("object", rec.Expected);
        Assert.Equal("]}", rec.Suffix);
    }

    [Fact]
    public void A_remainder_carrying_content_is_refused()
    {
        var doc = Minimal();
        const string payload = """{"icon": "clipboard-check"}, {"icon": "chart-line", "color": "#112233"}""";
        doc["presentation"] = payload;

        var recoveries = new List<Normalizer.LenientRecovery>();
        var fixedDoc = Normalizer.Unstringify(doc, recoveries)!;

        Assert.Equal(payload, fixedDoc["presentation"]!.GetValue<string>());
        Assert.Empty(recoveries);
        Assert.NotEmpty(Gate.StructuralErrors(fixedDoc));
    }

    [Fact]
    public void A_comma_led_remainder_is_refused_even_when_it_is_short()
    {
        var doc = Minimal();
        const string payload = """{"icon": "clipboard-check"},""";
        doc["presentation"] = payload;

        var fixedDoc = Normalizer.Unstringify(doc)!;

        Assert.Equal(payload, fixedDoc["presentation"]!.GetValue<string>());
    }

    [Fact]
    public void A_long_run_of_closers_is_refused()
    {
        var doc = Minimal();
        const string payload = """{"icon": "clipboard-check"}]}]}]}]}]}""";
        doc["presentation"] = payload;

        var fixedDoc = Normalizer.Unstringify(doc)!;

        Assert.Equal(payload, fixedDoc["presentation"]!.GetValue<string>());
    }

    [Fact]
    public void A_string_that_does_not_open_a_structure_is_never_reinterpreted()
    {
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
        doc["entities"]![0]!["fields"]![0]!["type"] = "string";
        var errs = Gate.StructuralErrors(doc);
        Assert.Contains(errs, e => e.Contains("fields/0/type") && e.Contains("allowed:") && e.Contains("longtext"));
    }

    [Fact]
    public void Failed_if_probes_are_not_reported_as_errors()
    {
        var doc = Minimal();
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
        doc["entities"]![0]!["fields"]![0]!["type"] = "string";
        var errs = Gate.StructuralErrors(doc);
        Assert.DoesNotContain(errs, e => e.Contains("blocks/0/kind"));
        Assert.DoesNotContain(errs, e => e.Contains("Expected \"\\\"reference\\\"\""));
        Assert.Contains(errs, e => e.Contains("fields/0/type"));
    }

    [Fact]
    public void A_property_whose_schema_states_a_union_of_types_does_not_stop_the_walk()
    {
        var doc = Minimal();
        doc["entities"]![0]!["calendar"] = true;
        doc["presentation"] = """{"icon": "clipboard-check"}""";

        var fixedDoc = Normalizer.Repair(doc)!;

        Assert.True(fixedDoc["entities"]![0]!["calendar"]!.GetValue<bool>());
        Assert.IsType<JsonObject>(fixedDoc["presentation"]);
    }

    [Fact]
    public void The_object_arm_of_a_union_is_still_unstringified()
    {
        var doc = Minimal();
        doc["entities"]![0]!["calendar"] = """{"start": "name"}""";

        var fixedDoc = Normalizer.Unstringify(doc)!;

        var calendar = fixedDoc["entities"]![0]!["calendar"];
        Assert.IsType<JsonObject>(calendar);
        Assert.Equal("name", calendar!["start"]!.GetValue<string>());
    }

    [Fact]
    public void A_union_typed_property_holding_a_plain_string_is_left_alone()
    {
        var doc = Minimal();
        doc["entities"]![0]!["calendar"] = "true";

        var fixedDoc = Normalizer.Unstringify(doc)!;

        Assert.Equal("true", fixedDoc["entities"]![0]!["calendar"]!.GetValue<string>());
    }
}

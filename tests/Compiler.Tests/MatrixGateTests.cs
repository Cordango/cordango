// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Compiler.Tests;

public class MatrixGateTests
{
    private static JsonObject Definition(JsonObject matrix, bool onDetail = false)
    {
        var page = onDetail ? """[ { "kind": "text", "value": "Week" } ]""" : $"[ {matrix.ToJsonString()} ]";
        var detail = onDetail ? $$""", "detail": { "blocks": [ {{matrix.ToJsonString()}} ] }""" : "";
        return (JsonObject)JsonNode.Parse($$"""
        {
          "schemaVersion": "2.0", "key": "hours", "name": "Hours", "version": "1.0.0",
          "entities": [
            { "key": "project", "label": "Project", "labelPlural": "Projects", "displayField": "name",
              "fields": [ { "key": "name", "label": "Name", "type": "text", "required": true } ] },
            { "key": "entry", "label": "Entry", "labelPlural": "Entries", "displayField": "note",
              "fields": [
                { "key": "note", "label": "Note", "type": "text" },
                { "key": "project", "label": "Project", "type": "reference", "targetEntity": "project", "onDelete": "restrict" },
                { "key": "person", "label": "Person", "type": "reference", "targetApp": "platform", "targetEntity": "person", "onDelete": "setNull" },
                { "key": "kind", "label": "Kind", "type": "select", "options": [ { "value": "a", "label": "A" }, { "value": "b", "label": "B" } ] },
                { "key": "day", "label": "Day", "type": "date" },
                { "key": "at", "label": "At", "type": "datetime" },
                { "key": "hours", "label": "Hours", "type": "decimal" },
                { "key": "double", "label": "Double", "type": "decimal", "computed": { "expr": "hours * 2" } }
              ]{{detail}} }
          ],
          "pages": [
            { "key": "week", "label": "Week", "entity": "entry",
              "state": [ { "key": "p", "type": "period", "default": "thisWeek" } ],
              "blocks": {{page}} }
          ],
          "roles": [
            { "key": "admin", "name": "Admin",
              "grants": [
                { "entity": "entry", "create": true, "read": true, "update": true, "delete": true },
                { "entity": "project", "create": true, "read": true, "update": true, "delete": true } ] }
          ]
        }
        """)!;
    }

    private static JsonObject Matrix(Action<JsonObject>? change = null)
    {
        var m = (JsonObject)JsonNode.Parse("""
        { "kind": "matrix",
          "source": { "entity": "entry", "aggregate": { "op": "sum", "field": "hours" },
            "filters": [ { "field": "day", "operator": "gte", "value": "{{state.p.from}}" },
                         { "field": "day", "operator": "lt", "value": "{{state.p.next}}" } ] },
          "rowBy": "project",
          "rowSource": { "entity": "project", "sort": [ { "field": "name", "direction": "asc" } ] },
          "blocks": [ { "kind": "field", "field": "name" } ],
          "columnBy": "day",
          "columnSource": { "dates": { "from": "{{state.p.from}}", "to": "{{state.p.to}}", "step": "day" } },
          "totals": { "rows": true, "columns": true, "share": true },
          "entries": { "fields": [ "note" ] },
          "editable": true }
        """)!;
        change?.Invoke(m);
        return m;
    }

    private static List<string> Errors(JsonObject matrix, bool onDetail = false) =>
        Gate.Validate(Definition(matrix, onDetail)).ToList();

    [Fact]
    public void A_week_of_hours_by_project_is_fine()
    {
        Assert.Empty(Errors(Matrix()));
    }

    [Fact]
    public void Rows_by_a_person_and_by_a_select_are_fine_with_their_own_row_sources()
    {
        Assert.Empty(Errors(Matrix(m => { m["rowBy"] = "person"; m["rowSource"] = JsonNode.Parse("""{ "platform": "person" }"""); m.Remove("blocks"); })));
        Assert.Empty(Errors(Matrix(m => { m["rowBy"] = "kind"; m["rowSource"] = JsonNode.Parse("""{ "options": { "entity": "entry", "field": "kind" } }"""); m.Remove("blocks"); })));
        Assert.Empty(Errors(Matrix(m => { m.Remove("rowSource"); m.Remove("blocks"); })));
    }

    [Fact]
    public void A_matrix_on_a_record_detail_is_refused()
    {
        Assert.Contains(Errors(Matrix(), onDetail: true), e => e.Contains("matrix belongs directly on a page"));
    }

    [Fact]
    public void The_source_must_be_a_local_entity_with_an_aggregate_of_a_number()
    {
        Assert.Contains(Errors(Matrix(m => m["source"]!["app"] = "other")), e => e.Contains("this app's own entity"));
        Assert.Contains(Errors(Matrix(m => m["source"]!.AsObject().Remove("aggregate"))), e => e.Contains("is what a cell shows"));
        Assert.Contains(Errors(Matrix(m => m["source"]!["aggregate"]!["field"] = "note")), e => e.Contains("which is not a number"));
        Assert.Contains(Errors(Matrix(m => m["source"]!["aggregate"]!["groupBy"] = "project")), e => e.Contains("the matrix's own job"));
    }

    [Fact]
    public void The_rows_must_come_from_what_rowBy_points_at()
    {
        Assert.Contains(Errors(Matrix(m => m["rowBy"] = "note")), e => e.Contains("must be a reference or a select"));
        Assert.Contains(Errors(Matrix(m => m["rowSource"] = JsonNode.Parse("""{ "entity": "entry" }"""))), e => e.Contains("any other rows would never hold a record"));
        Assert.Contains(Errors(Matrix(m => { m["rowBy"] = "person"; m["rowSource"] = JsonNode.Parse("""{ "entity": "project" }"""); m.Remove("blocks"); })),
            e => e.Contains("rowSource is { platform: 'person' }"));
        Assert.Contains(Errors(Matrix(m => { m["rowBy"] = "kind"; m["rowSource"] = JsonNode.Parse("""{ "entity": "project" }"""); m.Remove("blocks"); })),
            e => e.Contains("its rows are its options"));
    }

    [Fact]
    public void Row_header_blocks_are_checked_against_the_row_entity()
    {
        Assert.Contains(Errors(Matrix(m => m["blocks"] = JsonNode.Parse("""[ { "kind": "field", "field": "nope" } ]"""))),
            e => e.Contains("nope"));
    }

    [Fact]
    public void The_columns_must_be_a_date_field_over_a_dates_axis()
    {
        Assert.Contains(Errors(Matrix(m => m["columnBy"] = "at")), e => e.Contains("must be a date field"));
        Assert.Contains(Errors(Matrix(m => m["columnSource"] = JsonNode.Parse("""{ "entity": "project" }"""))), e => e.Contains("columnSource is a dates axis"));
        Assert.Contains(Errors(Matrix(m => m["columnSource"] = JsonNode.Parse("""{ "dates": { "from": "{{today}}", "count": 60, "step": "day" } }"""))),
            e => e.Contains("more than a month"));
        Assert.Contains(Errors(Matrix(m => m["columnSource"]!["dates"]!["to"] = "{{state.p.next}}")), e => e.Contains("one column too many"));
    }

    [Fact]
    public void A_share_needs_row_totals()
    {
        Assert.Contains(Errors(Matrix(m => m["totals"] = JsonNode.Parse("""{ "share": true }"""))), e => e.Contains("needs totals.rows"));
    }

    [Fact]
    public void Editing_needs_entries_a_sum_or_count_and_a_value_the_runtime_does_not_own()
    {
        Assert.Contains(Errors(Matrix(m => m.Remove("entries"))), e => e.Contains("'editable' needs 'entries'"));
        Assert.Contains(Errors(Matrix(m => m["source"]!["aggregate"]!["op"] = "avg")), e => e.Contains("needs a sum or a count"));
        Assert.Contains(Errors(Matrix(m => m["source"]!["aggregate"]!["field"] = "double")), e => e.Contains("which the runtime works out"));
        Assert.Contains(Errors(Matrix(m => m["entries"] = JsonNode.Parse("""{ "fields": [ "nope" ] }"""))), e => e.Contains("entries field 'nope'"));
        Assert.Empty(Errors(Matrix(m => { m["source"]!["aggregate"]!["op"] = "avg"; m.Remove("editable"); })));
    }

    [Fact]
    public void A_count_of_different_values_needs_a_field_but_not_a_number()
    {
        Assert.Empty(Errors(Matrix(m => { m["source"]!["aggregate"] = JsonNode.Parse("""{ "op": "countDistinct", "field": "note" }"""); m.Remove("editable"); })));
        Assert.Contains(Errors(Matrix(m => { m["source"]!["aggregate"] = JsonNode.Parse("""{ "op": "countDistinct" }"""); m.Remove("editable"); })),
            e => e.Contains("needs a 'field'"));
        Assert.Contains(Errors(Matrix(m => { m["source"]!["aggregate"] = JsonNode.Parse("""{ "op": "countDistinct", "field": "nope" }"""); m.Remove("editable"); })),
            e => e.Contains("is not a field of"));
        Assert.Contains(Errors(Matrix(m => m["source"]!["aggregate"] = JsonNode.Parse("""{ "op": "countDistinct", "field": "note" }"""))),
            e => e.Contains("needs a sum or a count"));
    }

    [Fact]
    public void The_matrix_is_taught_as_data_matrix_on_pages_only()
    {
        Assert.DoesNotContain("matrix", BlockKinds.NotAuthorable);
        Assert.Equal(["data.matrix"], BlockKinds.ByCanonical["matrix"]);
        Assert.True(Schemas.AuthoringSchema("page")["$defs"]!.AsObject().ContainsKey("block_data_matrix"));
        Assert.False(Schemas.AuthoringSchema("detail")["$defs"]!.AsObject().ContainsKey("block_data_matrix"));
    }
}

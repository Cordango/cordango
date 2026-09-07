// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Definition;

namespace Cordango.Compiler.Tests;

public class DirectoryRollupGateTests
{
    private static JsonObject Doc(string absencePerson, string allowancePerson) => (JsonObject)JsonNode.Parse($$"""
    {
      "schemaVersion": "2.0", "key": "leave", "name": "Leave", "version": "1.0.0",
      "entities": [
        { "key": "absence", "label": "Absence", "labelPlural": "Absences", "displayField": "kind",
          "fields": [
            { "key": "kind", "label": "Kind", "type": "text", "required": true },
            { "key": "requested_by", "label": "Person", "type": "reference", {{absencePerson}} },
            { "key": "days", "label": "Days", "type": "integer" },
            { "key": "starts", "label": "Starts", "type": "date" }
          ] },
        { "key": "allowance", "label": "Allowance", "labelPlural": "Allowances", "displayField": "year",
          "fields": [
            { "key": "year", "label": "Year", "type": "text", "required": true },
            { "key": "person", "label": "Person", "type": "reference", {{allowancePerson}} },
            { "key": "year_start", "label": "From", "type": "date" },
            { "key": "year_end", "label": "To", "type": "date" },
            { "key": "taken", "label": "Taken", "type": "integer",
              "computed": { "rollup": { "entity": "absence", "via": "requested_by", "match": "person", "op": "sum", "field": "days",
                                        "window": { "at": "starts", "within": { "from": "year_start", "to": "year_end" } } } } }
          ] },
        { "key": "team", "label": "Team", "labelPlural": "Teams", "displayField": "name",
          "fields": [ { "key": "name", "label": "Name", "type": "text", "required": true } ] }
      ],
      "views": [ { "key": "all", "label": "All", "type": "table", "entity": "allowance" } ],
      "roles": [ { "key": "admin", "name": "Admin",
                   "grants": [ { "entity": "*", "create": true, "read": true, "update": true, "delete": true } ] } ]
    }
    """)!;

    private const string Person = "\"targetApp\": \"platform\", \"targetEntity\": \"person\"";
    private const string Employee = "\"targetApp\": \"core_people\", \"targetEntity\": \"employee\"";
    private const string Team = "\"targetEntity\": \"team\"";

    [Fact]
    public void An_allowance_may_sum_the_absences_of_its_person_through_the_directory() =>
        Assert.Empty(Gate.SemanticErrors(Doc(Person, Person)));

    [Fact]
    public void The_shared_record_may_live_in_a_core_app() =>
        Assert.Empty(Gate.SemanticErrors(Doc(Employee, Employee)));

    [Fact]
    public void Both_sides_must_point_at_the_same_directory_record() =>
        Assert.Contains(Gate.SemanticErrors(Doc(Team, Person)),
            e => e.Contains("rollup via 'requested_by' must be a reference field on 'absence' pointing at 'person' in 'platform'"));

    [Fact]
    public void A_local_match_still_needs_a_local_via() =>
        Assert.Contains(Gate.SemanticErrors(Doc(Person, Team)),
            e => e.Contains("pointing at 'team'") && e.Contains("siblings"));
}

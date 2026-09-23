// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Compile;
using Cordango.SourceGen;
using Cordango.SourceGen.DotNet;

namespace Cordango.Standalone.Tests;

public class PeriodStandaloneTests
{
    private const string Definition = """
    {
      "schemaVersion": "2.0", "key": "hours", "name": "Hours", "version": "1.0.0",
      "entities": [
        { "key": "entry", "label": "Entry", "labelPlural": "Entries", "displayField": "note",
          "fields": [
            { "key": "note", "label": "Note", "type": "text" },
            { "key": "day", "label": "Day", "type": "date" },
            { "key": "hours", "label": "Hours", "type": "decimal" }
          ] }
      ],
      "pages": [
        { "key": "week", "label": "Week", "entity": "entry",
          "state": [ { "key": "p", "type": "period", "default": "thisWeek" } ],
          "blocks": [
            { "kind": "period", "stateKey": "p" },
            { "kind": "stat", "label": "This week",
              "source": { "entity": "entry", "aggregate": { "op": "sum", "field": "hours" },
                "filters": [
                  { "field": "day", "operator": "gte", "value": "{{state.p.from}}" },
                  { "field": "day", "operator": "lt", "value": "{{state.p.next}}" } ] } },
            { "kind": "stat", "label": "All time",
              "source": { "entity": "entry", "aggregate": { "op": "sum", "field": "hours" } } }
          ] }
      ],
      "roles": [
        { "key": "admin", "name": "Admin",
          "grants": [ { "entity": "entry", "create": true, "read": true, "update": true, "delete": true } ] }
      ]
    }
    """;

    private static GenerateResult Build()
    {
        var outcome = CandidateValidator.Run(JsonNode.Parse(Definition)!.AsObject(), "hours",
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.True(outcome.Manifest is not null, "the definition did not compile");
        return new DotNetVueGenerator().Generate(new GenerateRequest(
            new CompiledAppArtifact(outcome.Definition!.AsObject(), outcome.Manifest!,
                outcome.Hash ?? "unhashed", new CompilerInfo("test", "1")),
            new JsonObject { ["allowIncomplete"] = true, ["seed"] = 42 }));
    }

    private static string Page(GenerateResult result) =>
        string.Concat(result.Files.Where(f => f.RelativePath.StartsWith("web/src/", StringComparison.Ordinal))
            .Select(f => f.Content));

    [Fact]
    public void The_period_control_is_not_written_yet_and_says_so()
    {
        var result = Build();
        Assert.Contains("<UnsupportedBlock kind=\"period\"", Page(result), StringComparison.Ordinal);
        Assert.Contains(result.Warnings, d => d.Code == "CORD2301" && d.Message.Contains("'period' blocks", StringComparison.Ordinal));
    }

    [Fact]
    public void A_block_that_reads_the_period_is_left_out_instead_of_summing_all_time()
    {
        var result = Build();
        Assert.Contains("<UnsupportedBlock kind=\"stat\"", Page(result), StringComparison.Ordinal);
        Assert.Contains(result.Warnings, d => d.Code == "CORD2301"
            && d.Message.Contains("does not compute the period 'p'", StringComparison.Ordinal));
    }

    [Fact]
    public void A_block_that_does_not_read_the_period_is_drawn_as_before()
    {
        var page = Page(Build());
        Assert.Contains("All time", page, StringComparison.Ordinal);
        Assert.DoesNotContain("This week", page, StringComparison.Ordinal);
    }
}

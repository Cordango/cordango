// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

using Cordango.Definition;

namespace Cordango.Compiler.Tests;

/// <summary>
/// The gate must never THROW on a document. Refusing one is the whole job; falling over is not a
/// refusal, and a stack trace is not a diagnostic anybody can act on.
///
/// <para><b>The defect these pin.</b> A calendar's <c>timeAxis</c> was read with
/// <c>GetValue&lt;int&gt;()</c>, which throws on a node backed by a <c>long</c> — and whether a
/// number is one or the other depends on how the document was BUILT, not on what it says. Parsed
/// from a file it is JsonElement-backed and converts; assembled in code, which is what the CLI's
/// import lowering produces, it keeps the CLR type it was written with. So a definition that
/// validated perfectly from disk crashed <c>cordango import</c> with
/// <c>InvalidOperationException: A value of type 'System.Int64' cannot be converted to a
/// 'System.Int32'</c>, from inside the one assembly that is supposed to be safe to call on
/// anything.</para>
/// </summary>
public class GateNumberTests
{
    /// <summary>A document whose numbers are whatever the caller made them — the shape a lowering
    /// pass hands over, as opposed to the shape a parser produces.</summary>
    private static JsonObject WithTimeAxis(JsonNode startHour, JsonNode endHour) => new()
    {
        ["schemaVersion"] = "2.0",
        ["key"] = "num",
        ["name"] = "Numbers",
        ["version"] = "1.0.0",
        ["entities"] = new JsonArray
        {
            new JsonObject
            {
                ["key"] = "meeting",
                ["label"] = "Meeting",
                ["displayField"] = "title",
                ["fields"] = new JsonArray
                {
                    new JsonObject { ["key"] = "title", ["label"] = "Title", ["type"] = "text" },
                    new JsonObject { ["key"] = "at", ["label"] = "At", ["type"] = "datetime" },
                },
            },
        },
        ["pages"] = new JsonArray
        {
            new JsonObject
            {
                ["key"] = "diary",
                ["label"] = "Diary",
                ["blocks"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["kind"] = "calendar",
                        ["startField"] = "at",
                        ["range"] = "day",
                        ["source"] = new JsonObject { ["entity"] = "meeting" },
                        ["timeAxis"] = new JsonObject
                        {
                            ["startHour"] = startHour,
                            ["endHour"] = endHour,
                        },
                    },
                },
            },
        },
    };

    [Fact]
    public void A_time_axis_written_as_a_long_is_validated_rather_than_thrown_at()
    {
        // The exact shape that crashed: `cordango import` builds its document in code.
        var doc = WithTimeAxis(JsonValue.Create(8L), JsonValue.Create(19L));

        var errors = Record.Exception(() => Gate.SemanticErrors(doc));

        Assert.Null(errors);
        Assert.Empty(Gate.SemanticErrors(doc));
    }

    [Fact]
    public void An_axis_that_ends_before_it_begins_is_still_caught_when_written_as_a_long()
    {
        // The rule has to keep WORKING through the wider read, not merely stop crashing.
        var doc = WithTimeAxis(JsonValue.Create(19L), JsonValue.Create(8L));

        Assert.Contains(Gate.SemanticErrors(doc),
            e => e.Contains("endHour") && e.Contains("startHour"));
    }

    [Fact]
    public void The_same_document_parsed_from_text_behaves_identically()
    {
        // Two documents that SAY the same thing must be answered the same way, whatever backs
        // their numbers — that equivalence is the actual contract.
        var built = WithTimeAxis(JsonValue.Create(19L), JsonValue.Create(8L));
        var parsed = JsonNode.Parse(built.ToJsonString())!.AsObject();

        Assert.Equal(Gate.SemanticErrors(built), Gate.SemanticErrors(parsed));
    }
}

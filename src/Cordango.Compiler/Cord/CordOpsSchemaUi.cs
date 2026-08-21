// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Cord;

/// <summary>
/// The screen authoring vocabulary, as its OWN provider-neutral schema.
///
/// <para><b>Screens started in the domain schema and were moved out because they outgrew it</b>, and
/// the measurement is the reason rather than the instinct: when a screen stopped being
/// <c>{entity, label, view}</c> and became a question answered by sections, the domain schema went from
/// 10,724 to 13,469 bytes and tripped its 12,000 ceiling. The plan left "widen define_screens or give it
/// its own schema" explicitly open, to be settled by size. The size settled it.</para>
///
/// <para>That is rule 3 working the way it was meant to. The ceiling is not there to keep a number
/// small; it is there to make the moment a concern becomes its own concern impossible to miss.</para>
///
/// <para><b>No block vocabulary.</b> The corpus's 41 block kinds do not appear here and must not. A
/// screen says WHAT belongs on it; <see cref="CordLowerScreens"/> decides that four metrics become a row
/// of cards. In the hand-designed apps the top-level blocks are overwhelmingly containers — card 24,
/// stack 18, row 18, columns 11 — and asking an author to choose between them was never a question about
/// their application.</para>
/// </summary>
internal static class CordOpsSchemaUi
{
    public static JsonObject Ui() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray("ops"),
        ["properties"] = new JsonObject
        {
            ["note"] = Str("One line on what this change is for. Shown to the person waiting."),
            ["ops"] = new JsonObject
            {
                ["type"] = "array",
                ["minItems"] = 1,
                // The upsert rule is stated ONCE, here — see the note in CordOpsSchemaBehaviour.
                ["description"] =
                    "Applied in order. Either they all pass validation or none are kept. Each upsert "
                    + "adds one thing or replaces the one that already has its key, stated in full — "
                    + "anything omitted is cleared — and touches nothing else. Send several in one call.",
                ["items"] = new JsonObject
                {
                    ["oneOf"] = new JsonArray(
                        UpsertScreen(), RemoveScreen(), UpsertScreenTab(), RemoveScreenTab()),
                },
            },
        },
        ["$defs"] = new JsonObject { ["section"] = Section(), ["tab"] = Tab() },
    };

    private static JsonObject UpsertScreen() => Op("upsert_screen",
        "One screen people navigate to. "
        + "A screen answers ONE QUESTION somebody has — 'what needs my "
        + "attention', 'how are we doing', 'everything we know about this customer' — and it is made of "
        + "sections, each of which may draw on a DIFFERENT entity. Every app needs at least one screen. "
        // The hard rule, stated as a rule. The soft version ("do not need their own") was read as
        // advice and ignored: four of six apps in the first live run gave a child entity its own screen
        // and spent a repair round on the rejection.
        + "An entity that declares an owner cannot be a screen's subject: its rows render inside the "
        + "owner's detail. Name the OWNER instead. "
        + "Do not describe layout — say what belongs on the screen and in what order of importance, and "
        + "the arrangement is worked out for you.",
        new JsonObject
        {
            ["screen"] = new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                // Not `sections`: a screen may be all tabs. That it has at least ONE of the two is a
                // semantic rule with a sentence attached, so CordCheck says it — the schema says shape.
                ["required"] = new JsonArray("key", "label"),
                ["properties"] = new JsonObject
                {
                    ["key"] = Str(null),
                    ["label"] = Str("What this screen is called in navigation."),
                    ["subject"] = Str(
                        "The entity this screen is ABOUT, if there is one. Context only — a screen "
                        + "with a subject can still show sections over other entities, and a screen "
                        + "that spans several kinds of record has no subject at all."),
                    ["sections"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = Ref("#/$defs/section"),
                    },
                    ["tabs"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["description"] =
                            "Perspectives on the same records that share this screen. Sections above "
                            + "stay visible while these swap. Separate jobs are separate screens.",
                        ["items"] = Ref("#/$defs/tab"),
                    },
                },
            },
        },
        "screen");

    private static JsonObject RemoveScreen() => Op("remove_screen",
        "Drop a screen.",
        new JsonObject { ["key"] = Str("Screen key.") }, "key");

    private static JsonObject UpsertScreenTab() => Op("upsert_screen_tab",
        "Change ONE tab, leaving the screen's other tabs and shared sections alone. Sending the whole "
        + "screen instead would restate tabs somebody already approved.",
        new JsonObject { ["screen"] = Str("Screen key."), ["tab"] = Ref("#/$defs/tab") },
        "screen", "tab");

    private static JsonObject RemoveScreenTab() => Op("remove_screen_tab",
        "Drop one tab from a screen.",
        new JsonObject { ["screen"] = Str("Screen key."), ["tab"] = Str("Tab key.") },
        "screen", "tab");

    /// <summary>One named perspective of a screen. Keyed, because it is revised on its own.</summary>
    private static JsonObject Tab() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray("key", "label", "sections"),
        ["properties"] = new JsonObject
        {
            ["key"] = Str("Stable name. Keep it when the label changes."),
            ["label"] = Str("What the tab is called."),
            ["sections"] = new JsonObject
            {
                ["type"] = "array",
                ["minItems"] = 1,
                ["items"] = Ref("#/$defs/section"),
            },
        },
    };

    /// <summary>One part of a screen's answer. Four kinds, taken from what the hand-designed corpus apps
    /// actually use rather than from the 41-kind block catalog.</summary>
    private static JsonObject Section() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray("kind"),
        ["properties"] = new JsonObject
        {
            ["key"] = Str(
                "Stable name for this section, unique on its screen. Keep it when the label changes — "
                + "what people have saved about this list is filed under it."),
            ["kind"] = Enum(
                "list: records to work through. metric: one number. chart: a breakdown. text: a note. "
                + "split: two of those side by side, when they are read together.",
                [.. CordSectionKinds.All]),
            ["of"] = Str("The entity this section draws on. Sections on one screen may differ."),
            ["label"] = Str("The heading. For a list, also its name in navigation."),
            ["view"] = Enum("For kind=list: how the records are shown.", [.. CordVocabulary.Views.Words]),
            ["dateField"] = Str(
                "For view=calendar: the date field that places a record on the calendar. Required — an "
                + "entity often has several dates and the wrong one gives a calendar that looks right "
                + "and answers a different question."),
            ["filter"] = new JsonObject
            {
                ["type"] = "array",
                ["description"] =
                    "Which records this section is about. Use {{actor.id}} for the signed-in person — "
                    + "that is what makes 'my open tickets' different from 'all tickets', and showing "
                    + "somebody everything when they wanted their own is the most common way a screen "
                    + "is useless.",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["required"] = new JsonArray("field", "operator"),
                    ["properties"] = new JsonObject
                    {
                        ["field"] = Str(null),
                        ["operator"] = Enum(null,
                            "eq", "neq", "gt", "gte", "lt", "lte", "isEmpty", "isNotEmpty"),
                        ["value"] = new JsonObject { ["description"] = "What to compare against." },
                    },
                },
            },
            ["sort"] = new JsonObject
            {
                ["type"] = "array",
                ["description"] = "For kind=list. Put the most urgent row first.",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["required"] = new JsonArray("field"),
                    ["properties"] = new JsonObject
                    {
                        ["field"] = Str(null),
                        ["direction"] = Enum(null, "asc", "desc"),
                    },
                },
            },
            ["columns"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = Str(null),
                ["description"] =
                    "For kind=list as a table or a board: which fields to show. Leave out what the "
                    + "filter already implies — a list of MY tickets does not need an assignee column. "
                    + "A calendar shows a record's own name and takes none.",
            },
            ["groupBy"] = Str(
                "For view=board: the field the columns are. For kind=chart: what the breakdown splits by."),
            ["value"] = new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["required"] = new JsonArray("op"),
                ["description"] = "For kind=metric and kind=chart: what is counted or totalled.",
                ["properties"] = new JsonObject
                {
                    ["op"] = Enum(null, "count", "sum", "avg", "min", "max"),
                    ["field"] = Str("Required for everything except count."),
                },
            },
            ["text"] = Str("For kind=text."),
            ["visual"] = Enum(
                "For kind=chart. Leave it out unless the shape matters: a breakdown over TIME is a line "
                + "or an area, and nothing about the numbers says so.",
                [CordVocabulary.AutoVisual, .. CordVocabulary.ChartVisuals.Words]),
            ["sections"] = new JsonObject
            {
                ["type"] = "array",
                ["minItems"] = 2,
                ["maxItems"] = 2,
                ["items"] = Ref("#/$defs/section"),
                ["description"] =
                    "For kind=split: the two sections that sit side by side. Both must be content — a "
                    + "split inside a split is a grid, which this is not.",
            },
            ["ratio"] = new JsonObject
            {
                ["type"] = "array",
                ["minItems"] = 2,
                ["maxItems"] = 2,
                ["items"] = new JsonObject { ["type"] = "number", ["exclusiveMinimum"] = 0 },
                ["description"] =
                    "For kind=split: relative widths, e.g. [2,1]. Omit for equal, which is usually right.",
            },
        },
    };

    // ---- shared shapes ---------------------------------------------------------------------------

    // ---- helpers, mirroring CordOpsSchema so the concern schemas read identically -----------------

    private static JsonObject Op(string name, string description, JsonObject properties,
        params string[] required)
    {
        var props = new JsonObject { ["op"] = new JsonObject { ["const"] = name } };
        foreach (var (key, value) in properties.ToList())
        {
            properties.Remove(key);
            props[key] = value;
        }
        return new JsonObject
        {
            ["title"] = name,
            ["description"] = description,
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray(new[] { "op" }.Concat(required).Select(r => (JsonNode)r!).ToArray()),
            ["properties"] = props,
        };
    }

    private static JsonObject Str(string? description)
    {
        var o = new JsonObject { ["type"] = "string" };
        if (description is not null) o["description"] = description;
        return o;
    }

    private static JsonObject Enum(string? description, params string[] values)
    {
        var o = new JsonObject
        {
            ["type"] = "string",
            ["enum"] = new JsonArray(values.Select(v => (JsonNode)v!).ToArray()),
        };
        if (description is not null) o["description"] = description;
        return o;
    }

    private static JsonObject Ref(string pointer) => new() { ["$ref"] = pointer };
}

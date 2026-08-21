// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Cord;

/// <summary>
/// Screens → <c>pages</c> and <c>views</c>.
///
/// <para><b>This is where layout stops being the author's problem.</b> A screen is a list of sections;
/// what arrives at the runtime is a block tree with cards, rows and view references. The author never
/// picks a container, because picking one was never a decision about the application — in the
/// hand-designed corpus apps the top-level block kinds are card 24, stack 18, row 18, columns 11, all
/// carrying a much smaller set of content kinds underneath.</para>
///
/// <para>The one layout rule, and it is the only one: <b>consecutive metrics group into a row</b>. That
/// is the shape every hand-designed overview page uses — four cards of one stat each across the top —
/// and it is derivable from the sections rather than something to ask about. Everything else stacks in
/// the order the author wrote it, because that order is the author's actual statement about what
/// matters most.</para>
///
/// <para>Cord-authored screens and imported screens that exactly match this lowering reach here.
/// Other page shapes remain in the compatibility overlay, keeping import lossless without claiming
/// semantics Cord cannot safely edit.</para>
/// </summary>
internal static class CordLowerScreens
{
    public static (JsonArray Pages, JsonArray Views) Emit(IReadOnlyList<CordScreen> screens)
    {
        var pages = new JsonArray();
        var views = new JsonArray();
        var takenPageKeys = new HashSet<string>(StringComparer.Ordinal);

        for (var s = 0; s < screens.Count; s++)
        {
            var screen = screens[s];
            var pageKey = Unique(takenPageKeys, screen.Key ?? screen.Subject ?? $"screen_{s + 1}");
            var takenSectionKeys = new HashSet<string>(StringComparer.Ordinal);

            // One content section becomes one block. Shared, because a split's two sides lower by
            // exactly the same rules as a section standing alone — a chart beside another chart is still
            // that chart. Null only for a list with no entity, which has nothing to show.
            JsonObject? Content(CordSection section)
            {
                switch (section.Kind)
                {
                    case CordSectionKinds.Metric:
                        return Card(section.Label, new JsonObject
                        {
                            ["kind"] = "stat",
                            ["label"] = section.Label ?? Title(section.Of),
                            ["source"] = Source(section),
                        });

                    case CordSectionKinds.Chart:
                        return Card(section.Label, new JsonObject
                        {
                            ["kind"] = "chart",
                            ["chartType"] = ChartType(section),
                            ["source"] = Source(section),
                        });

                    case CordSectionKinds.Text:
                        return new JsonObject { ["kind"] = "text", ["text"] = section.Text ?? "" };

                    case CordSectionKinds.List:
                    default:
                    {
                        if (section.Of is not { } entity) return null;
                        var type = CordVocabulary.Views.LowerOr(section.View, "table");
                        // View identity is aggregate-local and authored. A label or position can
                        // change without renumbering a deep link, a personal view or a saved table
                        // preference. The check requires a key for view-emitting sections; the
                        // fallback only keeps lowering total over a hand-constructed invalid model.
                        var sectionKey = Unique(takenSectionKeys,
                            section.Key ?? $"{entity}_{type}");
                        var viewKey = $"{pageKey}__{sectionKey}";
                        views.Add(View(viewKey, section, entity, type));
                        return new JsonObject { ["kind"] = "view", ["view"] = viewKey };
                    }
                }
            }

            // One run of sections → one list of blocks. Shared by the screen's own sections and by each
            // tab's, because a chart inside a tab lowers by exactly the same rules as one outside it —
            // and metric grouping is per run, so a tab boundary ends a row rather than reaching across it.
            JsonArray Lower(IReadOnlyList<CordSection> sections)
            {
                var blocks = new JsonArray();
                var pending = new List<JsonObject>();

                void FlushMetrics()
                {
                    if (pending.Count == 0) return;
                    blocks.Add(new JsonObject
                    {
                        ["kind"] = "row",
                        ["blocks"] = new JsonArray(pending.Select(x => (JsonNode)x).ToArray()),
                    });
                    pending.Clear();
                }

                foreach (var section in sections)
                {
                    // A metric is the only thing that waits. Consecutive metrics collect into one row and
                    // everything else flushes them first, so the grouping never reaches across something
                    // the author put in between. That rule is RENDERER MECHANICS — derivable from the
                    // sections themselves, and therefore not a question anybody is asked.
                    if (section.Kind == CordSectionKinds.Metric)
                    {
                        if (Content(section) is { } card) pending.Add(card);
                        continue;
                    }

                    FlushMetrics();

                    // A SPLIT is the other side of that boundary, and the only arrangement an author
                    // states. Two things side by side is a claim that they are read TOGETHER, and nothing
                    // about a list of sections implies it. Everything else stays full-width and stacked in
                    // the order it was written, because that order is the author's statement about what
                    // matters.
                    if (section.Kind == CordSectionKinds.Split)
                    {
                        var sides = section.SectionList.Select(Content).OfType<JsonObject>().ToList();
                        if (sides.Count == 0) continue;

                        // One usable side is not a split. A single-column `columns` block would be refused
                        // by the block schema anyway (minItems 2), so the honest lowering is the block.
                        if (sides.Count == 1) { blocks.Add(sides[0]); continue; }

                        var columns = new JsonObject
                        {
                            ["kind"] = "columns",
                            ["columns"] = new JsonArray([.. sides.Select(x => (JsonNode)new JsonArray(x))]),
                        };
                        // Equal is the default, and 19 of the corpus's 21 two-column blocks use it — so an
                        // absent or equal ratio emits no weights at all rather than [1,1]. Same layout, and
                        // the document does not carry a decision nobody made.
                        if (section.Ratio is { Count: 2 } ratio && ratio[0] != ratio[1])
                            columns["weights"] = new JsonArray([.. ratio.Select(x => (JsonNode)x)]);
                        blocks.Add(columns);
                        continue;
                    }

                    if (Content(section) is { } block) blocks.Add(block);
                }

                FlushMetrics();
                return blocks;
            }

            var blocks = Lower(screen.SectionList);

            // Tabs come after the shared sections, which is what makes the headline row of the Budget
            // Planner's Hiring Plan stay put while the four perspectives swap beneath it.
            if (screen.TabList.Count > 0)
            {
                var tabs = new JsonArray();
                foreach (var tab in screen.TabList)
                    tabs.Add(new JsonObject
                    {
                        ["key"] = tab.Key,
                        ["label"] = tab.Label ?? Title(tab.Key),
                        ["blocks"] = Lower(tab.SectionList),
                    });
                blocks.Add(new JsonObject { ["kind"] = "tabs", ["tabs"] = tabs });
            }

            var page = new JsonObject
            {
                ["key"] = pageKey,
                ["label"] = screen.Label ?? Title(screen.Key ?? screen.Subject),
            };
            // A subject is CONTEXT, not content — it tells the runtime what the screen is about without
            // constraining what it shows. A screen spanning four entities simply has none.
            if (screen.Subject is { } subject) page["entity"] = subject;
            page["blocks"] = blocks;
            pages.Add(page);
        }

        return (pages, views);
    }

    /// <summary>
    /// How a chart is drawn: what the author said, or what the measure implies.
    ///
    /// <para>The inference is unchanged and stays the default — a grouped measure is a donut, an
    /// ungrouped one a bar — because it is right nearly always and asking would be asking for an answer
    /// nobody has. What it cannot know is that a breakdown runs over TIME. A monthly runway is a line,
    /// and no property of a measure separates "by month" from "by category". That is the whole reason
    /// <c>visual</c> exists, and why its default is <c>auto</c> rather than a named type: naming one
    /// would replace a good inference with a guess the author did not make.</para>
    /// </summary>
    private static string ChartType(CordSection section)
    {
        if (section.Visual is { } visual && visual != CordVocabulary.AutoVisual)
            return CordVocabulary.ChartVisuals.LowerOr(visual, visual);

        return section.GroupBy is not null || section.Value?.GroupBy is not null ? "donut" : "bar";
    }

    private static JsonObject View(string key, CordSection section, string entity, string type)
    {
        var view = new JsonObject
        {
            ["key"] = key,
            ["label"] = section.Label ?? Title(entity),
            ["type"] = type,
            ["entity"] = entity,
        };

        if (section.Filter is { Count: > 0 } filter)
            view["filters"] = new JsonArray(filter.Select(Filter).ToArray());

        if (section.Sort is { Count: > 0 } sort)
            view["sort"] = new JsonArray(sort.Select(x => (JsonNode)new JsonObject
            {
                ["field"] = x.Field,
                ["direction"] = x.Direction ?? "asc",
            }).ToArray());

        var config = new JsonObject();

        // A CALENDAR is placed by a DATE and shows a record's own name. It has no field list of any
        // kind, so `columns` had nowhere to go — it was emitted anyway and refused by the component
        // schema, which is the board defect at a second view type. What a calendar needs instead is the
        // one thing nobody could say: WHICH date. See CordSection.DateField.
        if (type == "calendar")
        {
            if (section.DateField is { } dateField) config["dateField"] = dateField;
        }
        else if (section.Columns is { Count: > 0 } columns)
        {
            // A LIST shows columns; a BOARD shows the same fields on a card, and the kanban component
            // calls them `cardFields`. Emitting `columns` there was accepted by Cord, refused by the
            // component schema, and the author's field choice was lost either way. One word from the
            // author, two places for it to land, and the lowerer decides which — the whole premise.
            config[type == "kanban" ? "cardFields" : "columns"] =
                new JsonArray(columns.Select(c => (JsonNode)JsonValue.Create(c)!).ToArray());
        }
        // A board has to be grouped by something, and the state field is what a board IS. The author
        // says "show these as a board"; which column headings that produces is mechanical.
        if (section.GroupBy is { } groupBy) config["groupByField"] = groupBy;
        if (config.Count > 0) view["config"] = config;

        return view;
    }

    private static JsonNode Filter(CordWhen w)
    {
        var o = new JsonObject();
        if (w.Field is not null) o["field"] = w.Field;
        if (w.Operator is not null) o["operator"] = w.Operator;
        if (w.Value is not null) o["value"] = w.Value.DeepClone();
        return o;
    }

    /// <summary>The <c>{entity, aggregate}</c> shape both <c>stat</c> and <c>chart</c> read.</summary>
    private static JsonObject Source(CordSection section)
    {
        var aggregate = new JsonObject { ["op"] = section.Value?.Op ?? "count" };
        if (section.Value?.Field is { } field) aggregate["field"] = field;
        if ((section.Value?.GroupBy ?? section.GroupBy) is { } groupBy) aggregate["groupBy"] = groupBy;

        var source = new JsonObject { ["entity"] = section.Of ?? "", ["aggregate"] = aggregate };
        if (section.Filter is { Count: > 0 } filter)
            source["filters"] = new JsonArray(filter.Select(Filter).ToArray());
        return source;
    }

    private static JsonObject Card(string? label, JsonObject inner)
    {
        var card = new JsonObject { ["kind"] = "card" };
        if (label is not null) card["label"] = label;
        card["blocks"] = new JsonArray(inner);
        return card;
    }

    /// <summary>Keys are generated, never authored, so they have to be unique BY CONSTRUCTION — two
    /// lists of the same entity on one screen is an ordinary thing to want ("open" and "closed"), and a
    /// silent key collision would make the second one replace the first at runtime.</summary>
    private static string Unique(HashSet<string> taken, string candidate)
    {
        if (taken.Add(candidate)) return candidate;
        for (var i = 2; ; i++)
        {
            var next = $"{candidate}_{i}";
            if (taken.Add(next)) return next;
        }
    }

    private static string Title(string? key) =>
        key is null ? "" : string.Join(' ', key.Split('_')
            .Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]));
}

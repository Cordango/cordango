// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Definition;

namespace Cordango.Cord;

/// <summary>
/// Raises the exact page/view shapes emitted by <see cref="CordLowerScreens"/> back into screen
/// semantics. Import is deliberately all-or-nothing for the two root arrays: arrays cannot be
/// overlaid by key, so claiming only some pages would make the raw remainder overwrite every page
/// the model emitted. Unsupported shapes stay untouched in the root overlay and still round-trip.
/// </summary>
internal static class CordImportScreens
{
    public static IReadOnlyList<CordScreen>? Take(JsonObject rest) => Attempt(rest).Screens;

    /// <summary>
    /// Why these pages and views could not be raised into screens — null when they could.
    ///
    /// <para><b>A silent <c>null</c> is how an entire screen layer went missing without anything
    /// anywhere saying so.</b> Refusing is correct: a partially claimed page array cannot round-trip.
    /// Refusing WORDLESSLY is not, because the consequence lands three layers away — Cord's outline
    /// and its inspect tool show no screens at all, and the review phase then asks a model to revise
    /// a screen it cannot read. It reads as the model failing. It is not.</para>
    ///
    /// <para>Runs on a copy: explaining must not consume the arrays it is explaining.</para>
    /// </summary>
    public static string? Explain(JsonObject raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        return Attempt((JsonObject)raw.DeepClone()).Refused;
    }

    private static (IReadOnlyList<CordScreen>? Screens, string? Refused) Attempt(JsonObject rest)
    {
        if (rest["pages"] is not JsonArray pages || pages.Count == 0)
            return (null, "the app has no pages to raise");
        if (rest["views"] is not JsonArray views)
            return (null, "the app has pages but no views array");
        if (pages.Any(x => x is not JsonObject) || views.Any(x => x is not JsonObject))
            return (null, "a page or view is not an object");

        try
        {
            var byKey = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            foreach (var view in views.OfType<JsonObject>())
            {
                if (Str(view, "key") is not { Length: > 0 } key)
                    return (null, "a view has no key");
                if (!byKey.TryAdd(key, view))
                    return (null, $"two views share the key '{key}'");
            }

            var used = new HashSet<string>(StringComparer.Ordinal);
            var screens = new List<CordScreen>();
            foreach (var page in pages.OfType<JsonObject>())
            {
                if (Page(page, byKey, used) is not { } screen)
                    return (null, $"page '{Str(page, "key") ?? "(no key)"}' uses a layout Cord has no "
                                + "operation for, so its sections cannot be named");
                screens.Add(screen);
            }

            // An unreferenced view is still application behaviour. Claiming pages while leaving that
            // view behind would make lowering emit one array and the overlay replace it with another.
            if (used.Count != byKey.Count)
                return (null, "these views belong to no page: "
                            + string.Join(", ", byKey.Keys.Where(k => !used.Contains(k))));

            // The parser above is intentionally small. This equality is the final authority: a page
            // is claimed only when lowering the raised model reproduces both arrays exactly.
            var emitted = CordLowerScreens.Emit(screens);
            if (DefinitionHash.Of(emitted.Pages) != DefinitionHash.Of(pages))
                return (null, "re-emitting the raised screens did not reproduce the pages exactly");
            if (DefinitionHash.Of(emitted.Views) != DefinitionHash.Of(views))
                return (null, "re-emitting the raised screens did not reproduce the views exactly");

            rest.Remove("pages");
            rest.Remove("views");
            return (screens, null);
        }
        catch (InvalidOperationException e) { return (null, $"a page could not be read: {e.Message}"); }
        catch (FormatException e) { return (null, $"a page could not be read: {e.Message}"); }
    }

    private static CordScreen? Page(JsonObject page, IReadOnlyDictionary<string, JsonObject> views,
        HashSet<string> used)
    {
        if (Str(page, "key") is not { Length: > 0 } key
            || Str(page, "label") is not { } label
            || page["blocks"] is not JsonArray blocks) return null;

        var sequence = new SectionKeys();
        var sections = new List<CordSection>();
        var tabs = new List<CordTab>();
        if (!Run(blocks, key, views, used, sequence, sections, tabs, allowTabs: true)) return null;

        return new CordScreen(key, label, Str(page, "entity"), sections,
            tabs.Count == 0 ? null : tabs);
    }

    private static bool Run(JsonArray blocks, string pageKey,
        IReadOnlyDictionary<string, JsonObject> views, HashSet<string> used, SectionKeys sequence,
        List<CordSection> sections, List<CordTab> tabs, bool allowTabs)
    {
        foreach (var block in blocks)
        {
            if (block is not JsonObject o || Str(o, "kind") is not { } kind) return false;

            if (kind == "row")
            {
                if (o["blocks"] is not JsonArray row || row.Count == 0) return false;
                foreach (var item in row)
                {
                    if (item is not JsonObject card || Metric(card, sequence) is not { } metric)
                        return false;
                    sections.Add(metric);
                }
                continue;
            }

            if (kind == "tabs")
            {
                if (!allowTabs || tabs.Count > 0 || o["tabs"] is not JsonArray tabNodes) return false;
                foreach (var node in tabNodes)
                {
                    if (node is not JsonObject tab || Str(tab, "key") is not { Length: > 0 } tabKey
                        || Str(tab, "label") is not { } tabLabel
                        || tab["blocks"] is not JsonArray tabBlocks) return false;
                    var tabSections = new List<CordSection>();
                    if (!Run(tabBlocks, pageKey, views, used, sequence, tabSections, [], allowTabs: false))
                        return false;
                    tabs.Add(new CordTab(tabKey, tabLabel, tabSections));
                }
                continue;
            }

            if (Content(o, pageKey, views, used, sequence) is not { } section) return false;
            sections.Add(section);
        }
        return true;
    }

    private static CordSection? Content(JsonObject block, string pageKey,
        IReadOnlyDictionary<string, JsonObject> views, HashSet<string> used, SectionKeys sequence)
    {
        return Str(block, "kind") switch
        {
            "view" => List(block, pageKey, views, used),
            "card" => Chart(block, sequence),
            "text" when Str(block, "text") is { } text =>
                new CordSection(Kind: CordSectionKinds.Text, Text: text),
            "columns" => Split(block, pageKey, views, used, sequence),
            _ => null,
        };
    }

    private static CordSection? Metric(JsonObject card, SectionKeys sequence)
    {
        if (Str(card, "kind") != "card" || card["blocks"] is not JsonArray { Count: 1 } blocks
            || blocks[0] is not JsonObject inner || Str(inner, "kind") != "stat"
            || inner["source"] is not JsonObject source) return null;

        var parsed = Source(source);
        if (parsed is null) return null;
        var (entity, value, groupBy, filters) = parsed.Value;
        return new CordSection(sequence.Next("metric"), CordSectionKinds.Metric, entity,
            Str(card, "label"), Filter: filters, GroupBy: groupBy, Value: value);
    }

    private static CordSection? Chart(JsonObject card, SectionKeys sequence)
    {
        if (Str(card, "kind") != "card" || card["blocks"] is not JsonArray { Count: 1 } blocks
            || blocks[0] is not JsonObject inner || Str(inner, "kind") != "chart"
            || inner["source"] is not JsonObject source || Str(inner, "chartType") is not { } chartType)
            return null;

        var parsed = Source(source);
        if (parsed is null) return null;
        var (entity, value, groupBy, filters) = parsed.Value;
        var inferred = groupBy is null ? "bar" : "donut";
        var visual = chartType == inferred ? null : CordVocabulary.ChartVisuals.Raise(chartType);
        if (chartType != inferred && visual is null) return null;

        return new CordSection(sequence.Next("chart"), CordSectionKinds.Chart, entity,
            Str(card, "label"), Filter: filters, GroupBy: groupBy, Value: value, Visual: visual);
    }

    private static CordSection? List(JsonObject block, string pageKey,
        IReadOnlyDictionary<string, JsonObject> views, HashSet<string> used)
    {
        if (Str(block, "view") is not { } viewKey || !views.TryGetValue(viewKey, out var view)
            || !used.Add(viewKey) || !viewKey.StartsWith(pageKey + "__", StringComparison.Ordinal)
            || viewKey[(pageKey.Length + 2)..] is not { Length: > 0 } sectionKey
            || Str(view, "entity") is not { } entity || Str(view, "type") is not { } loweredType
            || Str(view, "label") is not { } label) return null;

        var type = CordVocabulary.Views.Raise(loweredType);
        if (type is null) return null;

        var filters = Filters(view["filters"] as JsonArray);
        if (view["filters"] is JsonArray && filters is null) return null;
        var sort = Sorts(view["sort"] as JsonArray);
        if (view["sort"] is JsonArray && sort is null) return null;

        IReadOnlyList<string>? columns = null;
        string? dateField = null;
        string? groupBy = null;
        if (view["config"] is JsonObject config)
        {
            dateField = Str(config, "dateField");
            groupBy = Str(config, "groupByField");
            var columnName = loweredType == "kanban" ? "cardFields" : "columns";
            if (config[columnName] is JsonArray a)
            {
                var parsed = Strings(a);
                if (parsed is null) return null;
                columns = parsed;
            }
        }

        return new CordSection(sectionKey, CordSectionKinds.List, entity, label, type,
            filters, sort, columns, dateField, groupBy);
    }

    private static CordSection? Split(JsonObject block, string pageKey,
        IReadOnlyDictionary<string, JsonObject> views, HashSet<string> used, SectionKeys sequence)
    {
        if (block["columns"] is not JsonArray { Count: 2 } columns) return null;
        var sides = new List<CordSection>();
        foreach (var column in columns)
        {
            if (column is not JsonArray { Count: 1 } items || items[0] is not JsonObject item
                || Content(item, pageKey, views, used, sequence) is not { } side
                || side.Kind == CordSectionKinds.Split) return null;
            sides.Add(side);
        }

        IReadOnlyList<double>? ratio = null;
        if (block["weights"] is JsonArray weights)
        {
            var values = Numbers(weights);
            if (values is not { Count: 2 }) return null;
            ratio = values;
        }
        return new CordSection(Kind: CordSectionKinds.Split, Sections: sides, Ratio: ratio);
    }

    private static (string Entity, CordMeasure Value, string? GroupBy,
        IReadOnlyList<CordWhen>? Filters)? Source(JsonObject source)
    {
        if (Str(source, "entity") is not { } entity || source["aggregate"] is not JsonObject aggregate
            || Str(aggregate, "op") is not { } op) return null;
        var filters = Filters(source["filters"] as JsonArray);
        if (source["filters"] is JsonArray && filters is null) return null;
        return (entity, new CordMeasure(op, Str(aggregate, "field")), Str(aggregate, "groupBy"), filters);
    }

    private static IReadOnlyList<CordWhen>? Filters(JsonArray? nodes)
    {
        if (nodes is null) return null;
        var result = new List<CordWhen>();
        foreach (var node in nodes)
        {
            if (node is not JsonObject o) return null;
            result.Add(new CordWhen(Str(o, "field"), Str(o, "operator"), o["value"]?.DeepClone()));
        }
        return result;
    }

    private static IReadOnlyList<CordSort>? Sorts(JsonArray? nodes)
    {
        if (nodes is null) return null;
        var result = new List<CordSort>();
        foreach (var node in nodes)
        {
            if (node is not JsonObject o || Str(o, "field") is not { } field) return null;
            result.Add(new CordSort(field, Str(o, "direction")));
        }
        return result;
    }

    private static IReadOnlyList<string>? Strings(JsonArray nodes)
    {
        var result = new List<string>();
        foreach (var node in nodes)
            if (node is JsonValue v && v.TryGetValue<string>(out var value)) result.Add(value);
            else return null;
        return result;
    }

    private static IReadOnlyList<double>? Numbers(JsonArray nodes)
    {
        var result = new List<double>();
        foreach (var node in nodes)
            if (node is JsonValue v && v.TryGetValue<double>(out var value)) result.Add(value);
            else return null;
        return result;
    }

    private static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var value) ? value : null;

    private sealed class SectionKeys
    {
        private int _metric;
        private int _chart;
        public string Next(string kind) => kind == "metric" ? $"metric_{++_metric}" : $"chart_{++_chart}";
    }
}

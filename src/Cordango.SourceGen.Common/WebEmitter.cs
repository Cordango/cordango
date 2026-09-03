// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cordango.SourceGen.Common;

/// <summary>What the front-end emitter produced, and what it could not.</summary>
public sealed record WebResult(
    IReadOnlyList<GeneratedFile> Files,
    IReadOnlyList<Diagnostic> Warnings,
    IReadOnlyList<Diagnostic> Unsupported);

/// <summary>
/// The screens: one Vue component per page, one per record detail, plus the router and the model
/// the components read.
///
/// <para><b>Pages are expanded, not interpreted.</b> A block tree could be handed to a renderer that
/// walks it at runtime, and that would be less code here. It would also mean a person who wants to
/// change a screen has nothing to edit: the page would be data, and the only way to alter it would
/// be to alter the definition. Expanding the tree into a real component means the generated file is
/// a Vue file — readable, diffable, breakpoint-able, and yours to change once you stop
/// regenerating it.</para>
///
/// <para>The leaves are components that ship with the scaffold, so a page is a layout over
/// <c>&lt;StatCard&gt;</c> and <c>&lt;ViewBlock&gt;</c> rather than a wall of markup.</para>
/// </summary>
public static class WebEmitter
{
    /// <param name="capabilities">What this target can do at all, as opposed to what its emitters
    /// have got round to. A block the target will NEVER render — record history, which needs an audit
    /// trail this product does not keep — must not be reported as "not yet": somebody reading that
    /// would reasonably wait for a release that is never coming.</param>
    public static WebResult Emit(AppModel app, bool allowPartial, GeneratorCapabilities? capabilities = null)
    {
        ArgumentNullException.ThrowIfNull(app);

        Capabilities = capabilities;

        var files = new List<GeneratedFile>();
        var unsupported = new List<Diagnostic>();

        files.Add(AppModule(app));

        foreach (var page in app.Pages)
            files.Add(Page(app, page, unsupported));

        foreach (var entity in app.Entities)
            files.Add(RecordPage(app, entity, unsupported));

        files.Add(Router(app));

        return new WebResult(files, [], unsupported);
    }

    /// <summary>
    /// The definition, as the browser needs it: entities and fields, views, screens, processes and
    /// commands.
    ///
    /// <para>The same vocabulary the server enforces, so a filter written in a screen and a filter
    /// checked in a controller are the same words. Trimmed to what the front end actually reads —
    /// shipping the whole manifest would put the application's entire internal shape into a public
    /// bundle for no benefit.</para>
    /// </summary>
    private static GeneratedFile AppModule(AppModel app)
    {
        var model = new JsonObject
        {
            ["key"] = app.Key,
            ["name"] = app.Name,
            ["entities"] = new JsonArray([.. app.Entities.Select(Entity)]),
            ["views"] = new JsonArray([.. app.Views.Select(v => (JsonNode)v.Json.DeepClone())]),
            ["pages"] = new JsonArray([.. app.Pages.Select(p => (JsonNode)new JsonObject
            {
                ["key"] = p.Key,
                ["label"] = p.Label,
                ["icon"] = p.Icon,
                ["route"] = p.Route,
            })]),
            ["processes"] = new JsonArray([.. app.Processes.Select(p => (JsonNode)p.Json.DeepClone())]),
            ["commands"] = new JsonArray([.. app.Commands.Select(c => (JsonNode)c.Json.DeepClone())]),
        };

        var source = new Source(2);
        source.Line($"// {app.Name}, as the front end needs to see it: entities and their fields, saved views,");
        source.Line("// screens, commands and processes.");
        source.Line("//");
        source.Line("// Generated from the compiled App Definition. It is the SAME vocabulary the server enforces —");
        source.Line("// field keys, entity keys, filter operators — which is what lets a screen be read next to the");
        source.Line("// definition it came from. Regenerating replaces this file.");
        source.Line();
        source.Line("export const app = " + model.ToJsonString(Pretty));

        return new GeneratedFile("web/src/app.js", source.ToString());
    }

    private static JsonNode Entity(EntityModel entity) => new JsonObject
    {
        ["key"] = entity.Key,
        ["label"] = entity.Label,
        ["labelPlural"] = entity.LabelPlural,
        ["icon"] = entity.Icon,
        ["displayField"] = entity.DisplayField,
        ["fields"] = new JsonArray([.. entity.Fields.Select(f => (JsonNode)f.Json.DeepClone())]),
    };

    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        IndentCharacter = ' ',
        IndentSize = 2,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>One screen.</summary>
    private static GeneratedFile Page(AppModel app, PageModel page, List<Diagnostic> unsupported)
    {
        var body = new Source(2);
        var state = ScreenState(page);
        var context = new BlockContext(app, page.Entity, Record: false, $"$.pages[?(@.key=='{page.Key}')]")
        {
            State = state,
            PageTitle = page.Label,
        };

        body.Indent().Indent();
        foreach (var block in page.Blocks.OfType<JsonObject>())
            Block(body, block, context, unsupported);

        List<string> setup =
        [
            "// This screen came from the definition. Once you stop regenerating, it is an ordinary",
            "// Vue component and yours to change.",
        ];

        if (state.Count > 0)
        {
            setup.Add("");
            setup.Add("// The screen's own working state: which week, which facet, which mode. It is not data and");
            setup.Add("// it is not persisted — the blocks below write it, and the lists read it through their");
            setup.Add("// own `{{state.<key>}}` filters, which is how one filter bar drives several of them.");
            setup.Add("const state = reactive({");
            foreach (var (key, var) in state) setup.Add($"  {key}: {StateInitial(var)},");
            setup.Add("})");
        }

        return Component(
            path: $"web/src/pages/{page.ComponentName}.vue",
            imports: context.Imports,
            setup: setup,
            template: body.ToString(),
            title: page.Label,
            conditional: context.Conditional,
            state: state.Count > 0);
    }

    /// <summary>
    /// The screen's state vars, in the order they will be declared.
    ///
    /// <para>The declared list first, then any key a block WRITES that the screen forgot to declare.
    /// Refusing the second case would be defensible and unhelpful: a facet writing an undeclared key
    /// is a screen missing a line, not a screen asking for something this target cannot do, and an
    /// undeclared key is a text var — which is what a facet and a search box both are.</para>
    /// </summary>
    private static Dictionary<string, JsonObject> ScreenState(PageModel page)
    {
        var state = new Dictionary<string, JsonObject>(StringComparer.Ordinal);

        foreach (var var in page.State)
            if (AppModel.Str(var["key"]) is { } key) state.TryAdd(key, var);

        foreach (var key in Written(page.Blocks))
            state.TryAdd(key, new JsonObject { ["key"] = key, ["type"] = "text" });

        return state;
    }

    /// <summary>Every state key the blocks on a page write, however deep they are nested.</summary>
    private static IEnumerable<string> Written(JsonNode? blocks)
    {
        foreach (var block in AppModel.Arr(blocks).OfType<JsonObject>())
        {
            switch (AppModel.Str(block["kind"]))
            {
                case "filterbar":
                    if (AppModel.Str(block["search"]?["state"]) is { } search) yield return search;
                    foreach (var facet in AppModel.Arr(block["facets"]).OfType<JsonObject>())
                        if (AppModel.Str(facet["state"]) is { } key) yield return key;
                    break;

                case "control":
                    if (AppModel.Str(block["stateKey"]) is { } bound) yield return bound;
                    break;
            }

            // Containers, in every spelling the language has for holding children.
            foreach (var nested in Written(block["blocks"])) yield return nested;
            foreach (var tab in AppModel.Arr(block["tabs"]).OfType<JsonObject>())
                foreach (var nested in Written(tab["blocks"])) yield return nested;
            foreach (var column in AppModel.Arr(block["columns"]))
                foreach (var nested in Written(column is JsonArray ? column : column?["blocks"]))
                    yield return nested;
        }
    }

    /// <summary>What a state var holds before anybody touches it.</summary>
    private static string StateInitial(JsonObject var)
    {
        var value = var["default"];
        if (value is not null)
        {
            var literal = AppModel.Str(value);
            // The one token a default may carry. Resolved when the component is created rather than
            // written into the file, or a page built in December opens on December for ever.
            if (literal == "{{today}}") return "new Date().toISOString().slice(0, 10)";
            return literal is not null ? JsString(literal) : value.ToJsonString(Compact);
        }

        // An enum with no default opens on its first option: a segmented control has to be on
        // something, and `mandatory` would otherwise pick for us without saying so.
        if (AppModel.Str(var["type"]) == "enum"
            && AppModel.Arr(var["options"]).OfType<JsonObject>().FirstOrDefault() is { } first
            && AppModel.Str(first["value"]) is { } option)
            return "'" + option + "'";

        return AppModel.Str(var["type"]) == "number" ? "null" : "''";
    }

    /// <summary>
    /// One record, laid out the way the entity's <c>detail</c> says.
    ///
    /// <para>Its own component per entity rather than one generic record page, for the same reason
    /// pages are expanded: an invoice and a support ticket want different screens, and the place to
    /// say so is a file about invoices.</para>
    /// </summary>
    private static GeneratedFile RecordPage(AppModel app, EntityModel entity, List<Diagnostic> unsupported)
    {
        var body = new Source(2);
        var context = new BlockContext(app, entity.Key, Record: true, $"$.entities[?(@.key=='{entity.Key}')].detail");

        body.Indent().Indent();

        // Emitted unconditionally by the record-page template below, so it is imported
        // unconditionally too. It used to be registered only by the block that renders an edit
        // action, which left every page without an authored detail referencing a component it had
        // not imported.
        context.Imports.Add("RecordDialog");

        var blocks = AppModel.Arr(entity.Detail?["blocks"]).OfType<JsonObject>().ToList();
        if (blocks.Count == 0)
        {
            // No detail authored. Every field, which is what somebody opening a record wants when
            // nobody has said otherwise — and better than an empty page.
            //
            // With an Edit button, because the page below emits the edit dialog unconditionally and
            // without this nothing could ever open it: an entity with no authored detail had a
            // record page that could be read and never changed.
            context.Imports.Add("RecordFields");
            body.Line("<div class=\"d-flex justify-end\">");
            body.Line("  <v-btn size=\"small\" variant=\"tonal\" prepend-icon=\"mdi-pencil-outline\"");
            body.Line("    @click=\"editing = record\">Edit</v-btn>");
            body.Line("</div>");
            body.Line($"<RecordFields entity=\"{entity.Key}\" :record=\"record\" />");
        }
        else
        {
            foreach (var block in blocks) Block(body, block, context, unsupported);
        }

        var name = entity.PascalKey + "RecordPage";
        return Component(
            path: $"web/src/pages/{name}.vue",
            imports: context.Imports,
            setup: RecordSetup(entity),
            template: body.ToString(),
            title: null,
            record: true,
            conditional: context.Conditional);
    }

    private static string[] RecordSetup(EntityModel entity) =>
    [
        $"const entityKey = '{entity.Key}'",
        "",
        "const route = useRoute()",
        "const router = useRouter()",
        "const record = ref(null)",
        "const loading = ref(true)",
        "const error = ref(null)",
        "const editing = ref(null)",
        "",
        "async function load() {",
        "  loading.value = true",
        "  error.value = null",
        "  try {",
        "    record.value = await loadRecord(entityKey, route.params.id)",
        "  } catch (failure) {",
        "    error.value = failure.message",
        "  } finally {",
        "    loading.value = false",
        "  }",
        "}",
        "",
        "async function remove() {",
        "  await deleteRecord(entityKey, route.params.id)",
        "  // Back to where they came from, because the record they were looking at is gone. Staying",
        "  // would leave a page describing something that no longer exists.",
        "  router.back()",
        "}",
        "",
        "onMounted(load)",
        "watch(() => route.params.id, load)",
    ];

    /// <summary>Wrap a body in a single-file component with the imports it turned out to need.</summary>
    private static GeneratedFile Component(
        string path,
        IReadOnlySet<string> imports,
        IReadOnlyList<string> setup,
        string template,
        string? title,
        bool record = false,
        bool conditional = false,
        bool state = false)
    {
        var source = new Source(2);
        source.Line("<script setup>");

        if (record)
        {
            source.Line("import { ref, onMounted, watch } from 'vue'");
            source.Line("import { useRoute, useRouter } from 'vue-router'");
            source.Line(conditional
                ? "import { loadRecord, deleteRecord, visibleWhen } from '../records.js'"
                : "import { loadRecord, deleteRecord } from '../records.js'");
        }
        else
        {
            if (state) source.Line("import { reactive } from 'vue'");
            if (conditional) source.Line("import { visibleWhen } from '../records.js'");
        }

        foreach (var import in imports.OrderBy(i => i, StringComparer.Ordinal))
            source.Line($"import {import} from '../blocks/{import}.vue'");

        if (setup.Count > 0)
        {
            source.Line();
            foreach (var line in setup) source.Line(line);
        }

        source.Line("</script>");
        source.Line();
        source.Line("<template>");

        if (record)
        {
            source.Line("  <v-container fluid class=\"pa-6\">");
            source.Line("    <v-alert v-if=\"error\" type=\"error\" variant=\"tonal\">{{ error }}</v-alert>");
            source.Line("    <v-skeleton-loader v-else-if=\"loading\" type=\"article\" />");
            source.Line("    <div v-else class=\"d-flex flex-column ga-4\">");
            source.Lines(template.TrimEnd('\n'));
            source.Line("    </div>");
            source.Line();
            source.Line("    <RecordDialog");
            source.Line("      v-if=\"editing\"");
            source.Line("      :entity=\"entityKey\"");
            source.Line("      :record=\"editing\"");
            source.Line("      @close=\"editing = null\"");
            source.Line("      @saved=\"editing = null; load()\"");
            source.Line("    />");
            source.Line("  </v-container>");
        }
        else
        {
            source.Line($"  <PageShell title={Quote(title ?? "")}>");
            source.Lines(template.TrimEnd('\n'));
            source.Line("  </PageShell>");
        }

        source.Line("</template>");
        return new GeneratedFile(path, source.ToString());
    }

    /// <summary>What an emitting page has learned so far: which block components it needs, and what
    /// it is a page ABOUT.</summary>
    /// <summary>
    /// Why this block is a card saying so rather than the thing it asked for.
    ///
    /// <para>Two different answers, and the difference matters to whoever reads it. A block the
    /// emitters have not got to yet will appear in a later release. A block this TARGET cannot do —
    /// record history needs a field-level audit trail a standalone application does not keep, and
    /// related-apps needs other applications to be related to — will not, ever, and saying "not yet"
    /// would leave somebody waiting for a release that is never coming.</para>
    /// </summary>
    private static Diagnostic Unrenderable(string? kind, BlockContext context)
    {
        var name = kind ?? "?";

        if (Capabilities is { } caps && !caps.Blocks.Allows(name))
            return new Diagnostic(
                name switch
                {
                    "history" => DiagnosticCodes.HistoryBlock,
                    "relatedApps" => DiagnosticCodes.RelatedAppsBlock,
                    _ => DiagnosticCodes.UnsupportedBlock,
                },
                $"'{name}' block: {caps.Blocks.Explain(name)}.",
                context.Path);

        return new Diagnostic(NotYetCodes.Block,
            $"the dotnet-vue generator does not emit '{name}' blocks yet.", context.Path);
    }

    /// <summary>What the target can do, for the length of one emit. Set by <see cref="Emit"/>; the
    /// block walk is a deep recursion and threading one more argument through every level of it
    /// would cost more clarity than it buys.</summary>
    [ThreadStatic]
    private static GeneratorCapabilities? Capabilities;

    private sealed record BlockContext(AppModel App, string? Entity, bool Record, string Path)
    {
        public HashSet<string> Imports { get; } = Record ? [] : ["PageShell"];

        /// <summary>
        /// What the page shell has already written at the top of the screen.
        ///
        /// <para>A page called "All tasks" holding one list called "All tasks" printed those words
        /// twice, forty pixels apart, on most generated screens. The definition is not wrong to name
        /// both — a list has a name wherever it appears — so this is not something the author should
        /// have to work around. It is redundant only HERE, and only the emitter is in a position to
        /// know that.</para>
        /// </summary>
        public string? PageTitle { get; init; }

        /// <summary>The screen state vars in scope, by key. Empty on a record page, which has no
        /// state of its own: what a detail screen is about is the record.</summary>
        public IReadOnlyDictionary<string, JsonObject> State { get; init; } =
            new Dictionary<string, JsonObject>(StringComparer.Ordinal);

        /// <summary>What to hand a component that reads screen state — the object, or nothing.</summary>
        public string StateBinding => State.Count > 0 ? "state" : "null";

        /// <summary>Set when any block on this page has a <c>visibleWhen</c>, so the evaluator is
        /// imported only where it is used.</summary>
        public bool Conditional { get; set; }
    }

    /// <summary>
    /// One block, and its children.
    ///
    /// <para>Every branch either emits a component or records a diagnostic. There is no default that
    /// silently omits — a screen missing a third of itself with no explanation is the worst outcome
    /// available, because it looks finished.</para>
    /// </summary>
    private static void Block(Source source, JsonObject block, BlockContext context, List<Diagnostic> unsupported)
    {
        // A block the definition only shows sometimes. Wrapped rather than folded into the block's
        // own attributes, so it works the same for every kind — including the container blocks,
        // where hiding the wrapper has to hide the children too.
        if (block["visibleWhen"] is JsonObject condition)
        {
            context.Conditional = true;
            source.Line(
                $"<template v-if=\"visibleWhen({Js(condition)}, "
                + $"{(context.Record ? "record" : "null")}, {context.StateBinding})\">");
            source.Indent();

            var inner = (JsonObject)block.DeepClone();
            inner.Remove("visibleWhen");
            Block(source, inner, context, unsupported);

            source.Outdent();
            source.Line("</template>");
            return;
        }

        var kind = AppModel.Str(block["kind"]);

        switch (kind)
        {
            case "stack":
                Container(source, "BlockStack", $"gap=\"{AppModel.Str(block["gap"]) ?? "md"}\"", block["blocks"], context, unsupported);
                break;

            case "row":
                Container(source, "BlockRow", $"gap=\"{AppModel.Str(block["gap"]) ?? "md"}\"", block["blocks"], context, unsupported);
                break;

            case "section":
                Container(source, "BlockSection",
                    Attributes(("label", AppModel.Str(block["label"])), ("help", AppModel.Str(block["help"]))),
                    block["blocks"], context, unsupported);
                break;

            case "card":
                Container(source, "BlockCard",
                    Attributes(("label", AppModel.Str(block["label"])), ("padding", AppModel.Str(block["padding"])))
                    + (AppModel.Bool(block["bordered"]) ? " :bordered=\"true\"" : ""),
                    block["blocks"], context, unsupported);
                break;

            case "grid":
                Container(source, "BlockStack", "gap=\"md\"", block["blocks"], context, unsupported);
                break;

            case "columns":
                Columns(source, block, context, unsupported);
                break;

            case "tabs":
                Tabs(source, block, context, unsupported);
                break;

            case "text":
                if (AppModel.Str(block["value"]) is not { Length: > 0 }
                    && AppModel.Str(block["text"]) is not { Length: > 0 }) break;
                context.Imports.Add("BlockText");
                // The record goes with it wherever there is one, so a `{field}` in the line resolves.
                // A heading over a detail pane is the whole reason the language allows the token.
                source.Line($"<BlockText {Attributes(
                    ("text", AppModel.Str(block["value"]) ?? AppModel.Str(block["text"])),
                    ("size", AppModel.Str(block["size"])),
                    ("weight", AppModel.Str(block["weight"])),
                    ("color", AppModel.Str(block["color"])),
                    ("icon", AppModel.Str(block["icon"])),
                    ("entity", context.Record ? context.Entity : null))}"
                    + (context.Record ? " :record=\"record\"" : "") + " />");
                break;

            case "stat":
                Stat(source, block, context, unsupported);
                break;

            case "chart":
                context.Imports.Add("ChartBlock");
                Self(source, "ChartBlock",
                    Attributes(("label", AppModel.Str(block["label"])), ("chartType", AppModel.Str(block["chartType"]))),
                    Bind(("source", block["source"])));
                break;

            case "progress":
                Progress(source, block, context, unsupported);
                break;

            case "view":
                View(source, block, context, unsupported);
                break;

            case "table":
                Table(source, block, context, unsupported);
                break;

            case "split":
                Split(source, block, context, unsupported);
                break;

            case "intake":
                Intake(source, block, context);
                break;

            case "answers":
                Answers(source, block, context, unsupported);
                break;

            case "calendar":
                Calendar(source, block, context, unsupported);
                break;

            case "board":
                Board(source, block, context, unsupported);
                break;

            case "timeline":
                Timeline(source, block, context, unsupported);
                break;

            case "filterbar":
                context.Imports.Add("FilterBar");
                source.Line("<FilterBar");
                source.Indent();
                source.Line($"entity=\"{AppModel.Str(block["entity"]) ?? context.Entity}\"");
                source.Line($":state=\"{context.StateBinding}\"");
                if (block["search"] is JsonObject bar) source.Line($":search=\"{Js(bar)}\"");
                if (block["facets"] is JsonArray facets) source.Line($":facets=\"{Js(facets)}\"");
                source.Outdent();
                source.Line("/>");
                break;

            case "control":
                Control(source, block, context);
                break;

            case "child":
                Child(source, block, context, unsupported);
                break;

            case "hub":
                context.Imports.Add("RecordHub");
                context.Imports.Add("RecordDialog");
                source.Line("<RecordHub");
                source.Indent();
                source.Line($"entity=\"{context.Entity}\"");
                source.Line(":record=\"record\"");
                // Only when the definition named one. Written unconditionally, an absent title
                // arrived as `title=""`, the component looked up the field called "" , found
                // nothing, and fell back to printing the record's uuid as the page heading.
                if (AppModel.Str(block["title"]) is { Length: > 0 } heading)
                    source.Line($"title=\"{heading}\"");
                if (AppModel.Str(block["status"]) is { Length: > 0 } state)
                    source.Line($"status=\"{state}\"");
                source.Line($":facts=\"{JsArray(block["facts"])}\"");
                source.Line($":actions=\"{JsArray(block["actions"])}\"");
                source.Line("@changed=\"load\"");
                source.Line("@edit=\"editing = record\"");
                source.Line("@remove=\"remove\"");
                source.Outdent();
                source.Line("/>");
                break;

            case "fields":
                context.Imports.Add("RecordFields");
                source.Line($"<RecordFields entity=\"{context.Entity}\" :record=\"record\" :fields=\"{JsArray(block["fields"])}\" />");
                break;

            case "field":
                Field(source, block, context, unsupported);
                break;

            case "avatar":
                Avatar(source, block, context, unsupported);
                break;

            case "process":
                context.Imports.Add("RecordProcess");
                source.Line($"<RecordProcess entity=\"{context.Entity}\" :record=\"record\" />");
                break;

            case "create":
                context.Imports.Add("CreateButton");
                Self(source, "CreateButton", Attributes(
                    ("entity", AppModel.Str(block["entity"]) ?? context.Entity),
                    ("label", AppModel.Str(block["label"])),
                    ("icon", AppModel.Str(block["icon"])),
                    ("style", AppModel.Str(block["style"]))), "");
                break;

            case "chip":
                context.Imports.Add("BlockChip");
                source.Line($"<BlockChip {Attributes(
                    ("entity", AppModel.Str(block["entity"]) ?? context.Entity),
                    ("field", AppModel.Str(block["field"])),
                    ("value", AppModel.Str(block["value"])))} :record=\"record\" />");
                break;

            case "action":
                context.Imports.Add("ActionButton");
                source.Line(
                    $"<ActionButton entity=\"{AppModel.Str(block["entity"]) ?? context.Entity}\" "
                    + $"command=\"{AppModel.Str(block["command"])}\" :record=\"record\" />");
                break;

            case "tiles":
                context.Imports.Add("TilesBlock");
                source.Line(
                    $"<TilesBlock entity=\"{AppModel.Str(block["entity"]) ?? context.Entity}\" "
                    + $":record=\"record\" :tiles=\"{JsArray(block["tiles"])}\" />");
                break;

            // A settings entity holds ONE row rather than a list, so the block is the row itself.
            // It loads and saves on its own — there is nothing to pick between and no table to open.
            case "settings":
                context.Imports.Add("SettingsBlock");
                source.Line(
                    $"<SettingsBlock entity=\"{AppModel.Str(block["entity"]) ?? context.Entity}\""
                    + $"{Echoes(context.App.Entity(AppModel.Str(block["entity"]) ?? context.Entity)?.Label, context)} />");
                break;

            case "repeat":
                Repeat(source, block, context, unsupported);
                break;

            default:
                context.Imports.Add("UnsupportedBlock");
                source.Line($"<UnsupportedBlock kind=\"{kind}\" />");
                unsupported.Add(Unrenderable(kind, context));
                break;
        }
    }

    /// <summary>
    /// A saved view, rendered the way its <c>type</c> says.
    ///
    /// <para>The type is not decoration. A calendar view and a table view are the same query — same
    /// entity, same filters, same permissions — and differ only in the arrangement, which is exactly
    /// why rendering every view as a table would be so quiet a wrong answer: the rows would be
    /// right, the screen would look finished, and the thing the author asked for would be missing
    /// with nothing to notice.</para>
    /// </summary>
    private static void View(Source source, JsonObject block, BlockContext context, List<Diagnostic> unsupported)
    {
        var key = AppModel.Str(block["view"]);
        var view = context.App.View(key);

        // No such view is a broken definition rather than a gap in this target, and the component
        // says so on the screen. Refusing here would report it as something we have not built yet.
        switch (view?.Type ?? "table")
        {
            case "table":
                context.Imports.Add("ViewBlock");
                source.Line(
                    $"<ViewBlock view=\"{key}\" :state=\"{context.StateBinding}\""
                    // Written only to turn it OFF. The component defaults to true, so emitting the
                    // affirmative case would put `:create="true"` on every table ever generated and
                    // make a no-op setting look like a decision somebody took.
                    + (view?.NewButton == false ? " :create=\"false\"" : "")
                    + $"{Echoes(view?.Label, context)} />");
                break;

            case "calendar":
                context.Imports.Add("CalendarBlock");
                source.Line($"<CalendarBlock view=\"{key}\" :state=\"{context.StateBinding}\" />");
                break;

            case "kanban":
                context.Imports.Add("BoardBlock");
                source.Line(
                    $"<BoardBlock view=\"{key}\" :state=\"{context.StateBinding}\""
                    + $"{Echoes(view?.Label, context)} />");
                break;

            case "timeline":
                context.Imports.Add("TimelineBlock");
                source.Line(
                    $"<TimelineBlock view=\"{key}\" :state=\"{context.StateBinding}\""
                    + $"{Echoes(view?.Label, context)} />");
                break;

            default:
                context.Imports.Add("UnsupportedBlock");
                source.Line($"<UnsupportedBlock kind=\"{view!.Type} view\" />");
                unsupported.Add(NotYet(
                    $"'{key}' is a '{view.Type}' view, and 'dashboard' and 'detail' views do not render "
                    + "as a block", context));
                break;
        }
    }

    /// <summary>
    /// A list the block itself describes: which rows, in what order, showing which columns.
    ///
    /// <para>What goes to the component is a view definition — the same shape a saved view has — so
    /// one component renders both and there is no second path on which a filter can be dropped. An
    /// earlier version of this pointed the block at a synthesized <c>&lt;entity&gt;_table</c> view
    /// instead, on the belief that the compiler always makes one. It does not: it synthesizes them
    /// only for a definition that declares no views at all. On every other application the block
    /// named a view that did not exist, and where one happened to exist it rendered the entire
    /// table — the block's own filters, sort, columns and limit all silently gone.</para>
    /// </summary>
    private static void Table(Source source, JsonObject block, BlockContext context, List<Diagnostic> unsupported)
    {
        var query = block["source"] as JsonObject;
        var entity = AppModel.Str(query?["entity"]) ?? AppModel.Str(block["entity"]) ?? context.Entity;

        foreach (var gap in Gaps(block, query, context.Record, tabular: true))
            unsupported.Add(NotYet(gap, context));

        // The one cell inline editing cannot offer. A process-governed status moves by running a
        // transition, and a dropdown over the field's options would offer moves the lifecycle
        // forbids — which the server would then refuse, one click at a time.
        if (AppModel.Bool(block["inlineEdit"])
            && context.App.Processes.FirstOrDefault(p => p.Entity == entity) is { } process
            && Columns(block).Contains(process.StateField))
            unsupported.Add(NotYet(
                $"inline editing '{process.StateField}' — it is governed by the '{process.Key}' process, "
                + "so changing it has to run a transition", context));

        context.Imports.Add("ViewBlock");
        source.Line("<ViewBlock");
        source.Indent();
        source.Line($":definition=\"{Js(Query(block, query, entity, "table", context.Record))}\"");
        source.Line($":state=\"{context.StateBinding}\"");
        if (context.Record) source.Line(":record=\"record\"");
        if (block["search"] is JsonObject search) source.Line($":search=\"{Js(search)}\"");
        if (block["groupBy"] is JsonObject group) source.Line($":group-by=\"{Js(group)}\"");
        if (block["newDefaults"] is JsonObject defaults) source.Line($":new-defaults=\"{Js(defaults)}\"");
        if (block["filterBar"] is JsonObject bar) source.Line($":filter-bar=\"{Js(bar)}\"");
        if (AppModel.Bool(block["allowDelete"])) source.Line(":allow-delete=\"true\"");
        if (AppModel.Bool(block["inlineEdit"])) source.Line(":inline-edit=\"true\"");
        if (AppModel.Bool(block["openDetail"])) source.Line(":open-detail=\"true\"");
        if (AppModel.Str(block["orderField"]) is { } order) source.Line($"order-field=\"{order}\"");
        if (Echoes(AppModel.Str(block["label"]), context) is { Length: > 0 }) source.Line(":hide-title=\"true\"");
        // A page-level list creates through the New button. `inlineCreate` defaults on in the
        // language, so a table only loses the button where somebody has turned it off.
        if (block["inlineCreate"] is not null && !AppModel.Bool(block["inlineCreate"])
            && !AppModel.Bool(block["newButton"]))
            source.Line(":create=\"false\"");
        source.Outdent();
        source.Line("/>");
    }

    /// <summary>
    /// The rows of another entity that point back at this record.
    ///
    /// <para><b>The definition spells the foreign key <c>via</c>.</b> This read <c>field</c>, which
    /// no child block has ever carried, so every child list in every generated application was
    /// emitted with an empty one — and an empty field key is not an empty filter, it is a filter on
    /// a column called "". The server refused each one by name and the screen showed
    /// <c>'segment' has no field ''</c> where the list should have been. Every detail screen with a
    /// child list on it, in every application, since the block was first emitted.</para>
    ///
    /// <para>It goes through the same <see cref="Query"/> as a <c>table</c> block, so a child list
    /// is an ordinary list that happens to be narrowed by its parent: the block's own columns,
    /// label, limit and toolbar all arrive the way a table's do. The component it used to point at
    /// read none of them — it looked up the first saved view over the child entity and rendered
    /// that instead, which showed the wrong columns where a view existed and nothing at all where
    /// one did not.</para>
    /// </summary>
    private static void Child(Source source, JsonObject block, BlockContext context, List<Diagnostic> unsupported)
    {
        var entity = AppModel.Str(block["entity"]);
        var via = AppModel.Str(block["via"]);

        // `via` and the block's row cap are the only two things Query reads off a source object. A
        // child block carries them itself, so they are handed over in the shape it expects rather
        // than giving Query a second place to look.
        var query = new JsonObject { ["via"] = via };
        if (block["limit"] is { } limit) query["limit"] = limit.DeepClone();

        foreach (var gap in Gaps(block, query, context.Record, tabular: true))
            unsupported.Add(NotYet(gap, context));

        // Only the table presentation is built. The others are real differences in how the rows are
        // read — a feed is chronological with a composer, a checklist is one tick per row — so a
        // table in their place is a list of the right records in the wrong shape, and that is worth
        // saying rather than leaving somebody to notice.
        var childType = AppModel.Str(block["childType"]);
        if (childType is not null and not "table")
            unsupported.Add(NotYet($"a child list's '{childType}' presentation (the rows render as a table)", context));

        context.Imports.Add("ViewBlock");
        source.Line("<ViewBlock");
        source.Indent();
        source.Line($":definition=\"{Js(Query(block, query, entity, "table", context.Record))}\"");
        source.Line($":state=\"{context.StateBinding}\"");
        source.Line(":record=\"record\"");
        if (block["groupBy"] is JsonObject group) source.Line($":group-by=\"{Js(group)}\"");
        if (block["filterBar"] is JsonObject bar) source.Line($":filter-bar=\"{Js(bar)}\"");
        if (AppModel.Bool(block["allowDelete"])) source.Line(":allow-delete=\"true\"");
        if (AppModel.Bool(block["inlineEdit"])) source.Line(":inline-edit=\"true\"");
        if (AppModel.Bool(block["openDetail"])) source.Line(":open-detail=\"true\"");
        if (AppModel.Str(block["orderField"]) is { } order) source.Line($"order-field=\"{order}\"");
        if (Echoes(AppModel.Str(block["label"]), context) is { Length: > 0 }) source.Line(":hide-title=\"true\"");
        // `inlineCreate` defaults on in the language, so the New button goes only where somebody
        // has turned it off.
        if (block["inlineCreate"] is not null && !AppModel.Bool(block["inlineCreate"]))
            source.Line(":create=\"false\"");
        source.Outdent();
        source.Line("/>");
    }

    /// <summary>The front door: the forms somebody can fill in, and the form itself once they pick
    /// one. Collection binding — pair it with a filter so retired forms stay off the list.</summary>
    private static void Intake(Source source, JsonObject block, BlockContext context)
    {
        var forms = context.App.Forms;
        var entity = AppModel.Str(block["entity"]) ?? forms?.TemplateEntity ?? context.Entity;

        context.Imports.Add("IntakeBlock");
        source.Line("<IntakeBlock");
        source.Indent();
        source.Line($"entity=\"{entity}\"");
        if (AppModel.Str(block["label"]) is { } label) source.Line($"label=\"{label}\"");
        if (AppModel.Arr(block["filters"]) is { Count: > 0 } filters)
            source.Line($":filters=\"{Js(filters)}\"");
        source.Outdent();
        source.Line("/>");
    }

    /// <summary>
    /// The answers the record was filed with. Record binding only.
    ///
    /// <para>Everything it needs to walk from the record to its answers is in the forms descriptor,
    /// so the component takes the field keys as props rather than looking anything up: the same
    /// build-time resolution the server side does, for the same reason.</para>
    /// </summary>
    private static void Answers(
        Source source, JsonObject block, BlockContext context, List<Diagnostic> unsupported)
    {
        if (context.App.Forms is not { } forms)
        {
            unsupported.Add(NotYet("an 'answers' block in an application with no forms", context));
            return;
        }

        // `via` names the reference on THIS entity pointing at the submission. Omitted, it is the one
        // the descriptor already resolved for this entity.
        var via = AppModel.Str(block["via"])
            ?? (context.Entity is { } key && forms.BackReferences.TryGetValue(key, out var found) ? found : null);
        if (via is null)
        {
            unsupported.Add(NotYet(
                "an 'answers' block on an entity with no reference to the form submission", context));
            return;
        }

        context.Imports.Add("AnswersBlock");
        source.Line("<AnswersBlock");
        source.Indent();
        source.Line($"via=\"{via}\"");
        source.Line($"answer-entity=\"{forms.AnswerEntity}\"");
        source.Line($"answer-response-field=\"{forms.AnswerResponse}\"");
        source.Line($"answer-question-field=\"{forms.AnswerQuestion}\"");
        source.Line($"answer-value-field=\"{forms.AnswerValue}\"");
        source.Line($"question-entity=\"{forms.QuestionEntity}\"");
        if (forms.QuestionText is { } text) source.Line($"question-text-field=\"{text}\"");
        if (forms.QuestionOrder is { } order) source.Line($"question-order-field=\"{order}\"");
        if (AppModel.Str(block["label"]) is { } label) source.Line($"label=\"{label}\"");
        source.Line(":record=\"record\"");
        source.Outdent();
        source.Line("/>");
    }

    /// <summary>
    /// The inbox: a list of records on the left, the selected one's detail on the right.
    ///
    /// <para>The block's own <c>blocks</c> are emitted into the component's <c>detail</c> slot, whose
    /// slot prop is named <c>record</c> — which is the same identifier a record page binds, so every
    /// block that draws on a detail screen draws here with no knowledge that it is inside a split.
    /// That is the whole trick, and it is why this needs no per-block special casing.</para>
    ///
    /// <para>The children are emitted with <c>Record: true</c> even on a collection page: inside the
    /// slot there IS a record, and without it a `fields` block would render against the page instead
    /// of against the selection.</para>
    /// </summary>
    private static void Split(Source source, JsonObject block, BlockContext context, List<Diagnostic> unsupported)
    {
        var query = block["source"] as JsonObject;
        var entity = AppModel.Str(query?["entity"]) ?? context.Entity;

        foreach (var gap in Gaps(block, query, context.Record, tabular: true))
            unsupported.Add(NotYet(gap, context));

        context.Imports.Add("SplitBlock");
        source.Line("<SplitBlock");
        source.Indent();
        source.Line($":definition=\"{Js(Query(block, query, entity, "split", context.Record))}\"");
        source.Line($":state=\"{context.StateBinding}\"");
        if (context.Record) source.Line(":record=\"record\"");
        if (AppModel.Arr(block["fields"]) is { Count: > 0 } fields)
            source.Line($":fields=\"{JsArray(fields)}\"");
        if (Echoes(AppModel.Str(block["label"]), context) is not { Length: > 0 }
            && AppModel.Str(block["label"]) is { } label)
            source.Line($"label=\"{label}\"");
        if (AppModel.Str(block["emptyText"]) is { } empty) source.Line($"empty-text=\"{empty}\"");
        source.Outdent();
        source.Line(">");
        source.Indent();

        // `as` names the current item for descendants. The slot prop is always `record`, so a name
        // is accepted and ignored rather than refused: the blocks inside address the selection the
        // way they address any record, and nothing in a generated application reads `{{lead_sel.x}}`.
        if (AppModel.Str(block["as"]) is { } alias)
            unsupported.Add(NotYet($"a split's `as: '{alias}'` name (its blocks read the selection as the record)", context));

        source.Line("<template #detail=\"{ record }\">");
        source.Indent();

        // Imports are the SAME set as the outer context — `with` copies the reference — so a
        // component only the detail blocks use is still imported at the top of the page.
        var inner = context with { Record = true, Entity = entity };
        foreach (var child in AppModel.Arr(block["blocks"]).OfType<JsonObject>())
            Block(source, child, inner, unsupported);

        source.Outdent();
        source.Line("</template>");
        source.Outdent();
        source.Line("</SplitBlock>");

        // `Conditional` is a mutable flag and `with` copies it BY VALUE, so a `visibleWhen` on a
        // block inside the detail pane would otherwise not import the evaluator the page needs.
        if (inner.Conditional) context.Conditional = true;
    }

    /// <summary>A month grid over records that carry a date, from the block's own query.</summary>
    private static void Calendar(Source source, JsonObject block, BlockContext context, List<Diagnostic> unsupported)
    {
        var query = block["source"] as JsonObject;
        var entity = AppModel.Str(query?["entity"]) ?? context.Entity;

        foreach (var gap in Gaps(block, query, context.Record)) unsupported.Add(NotYet(gap, context));

        var range = AppModel.Str(block["range"]);
        if (range is not null and not "month")
            unsupported.Add(NotYet($"a calendar's '{range}' range (only the month grid renders)", context));
        if (block["quickAdd"] is not null)
            unsupported.Add(NotYet("a calendar's 'quickAdd' — a new entry opens the full record form", context));

        var definition = Query(block, query, entity, "calendar", context.Record);
        var config = (JsonObject)definition["config"]!;
        if (AppModel.Str(block["startField"]) is { } start) config["dateField"] = start;
        if (AppModel.Str(block["endField"]) is { } end) config["endField"] = end;

        context.Imports.Add("CalendarBlock");
        source.Line("<CalendarBlock");
        source.Indent();
        source.Line($":definition=\"{Js(definition)}\"");
        source.Line($":state=\"{context.StateBinding}\"");
        if (context.Record) source.Line(":record=\"record\"");
        if (!AppModel.Bool(block["allowCreate"])) source.Line(":create=\"false\"");
        source.Outdent();
        source.Line("/>");
    }

    /// <summary>
    /// A board: the same rows a table would show, stacked into columns by one field.
    ///
    /// <para>The presentation goes into the definition's <c>config</c> under the same names a saved
    /// kanban view uses, so the component reads one shape whichever route the board arrived by. The
    /// block spells the column field <c>groupField</c> and a saved view spells it
    /// <c>groupByField</c>; both are written, because the component that reads them is the only
    /// place that should have to know they are the same thing.</para>
    /// </summary>
    private static void Board(Source source, JsonObject block, BlockContext context, List<Diagnostic> unsupported)
    {
        var query = block["source"] as JsonObject;
        var entity = AppModel.Str(query?["entity"]) ?? AppModel.Str(block["entity"]) ?? context.Entity;

        foreach (var gap in Gaps(block, query, context.Record)) unsupported.Add(NotYet(gap, context));

        var definition = Query(block, query, entity, "kanban", context.Record);
        var config = (JsonObject)definition["config"]!;
        if (AppModel.Str(block["groupField"]) is { } group) config["groupByField"] = group;
        if (block["cardFields"] is JsonArray cards) config["cardFields"] = cards.DeepClone();
        if (AppModel.Str(block["sumField"]) is { } sum) config["sumField"] = sum;
        if (AppModel.Str(block["interaction"]) is { } interaction) config["interaction"] = interaction;

        context.Imports.Add("BoardBlock");
        source.Line("<BoardBlock");
        source.Indent();
        source.Line($":definition=\"{Js(definition)}\"");
        source.Line($":state=\"{context.StateBinding}\"");
        if (context.Record) source.Line(":record=\"record\"");
        if (block["search"] is JsonObject search) source.Line($":search=\"{Js(search)}\"");
        if (AppModel.Bool(block["newButton"])) source.Line(":create=\"true\"");
        if (Echoes(AppModel.Str(block["label"]), context) is { Length: > 0 }) source.Line(":hide-title=\"true\"");
        source.Outdent();
        source.Line("/>");
    }

    /// <summary>
    /// Bars along a date axis, one lane per whatever groups them.
    ///
    /// <para>Unlike a board, the block names its entity directly rather than carrying a
    /// <c>source</c> — so there is no query object to read <c>via</c> from, and a timeline on a
    /// record screen is narrowed by nothing. That is the language's shape, not an omission here.
    /// </para>
    /// </summary>
    private static void Timeline(Source source, JsonObject block, BlockContext context, List<Diagnostic> unsupported)
    {
        var entity = AppModel.Str(block["entity"]) ?? context.Entity;

        foreach (var gap in Gaps(block, null, context.Record)) unsupported.Add(NotYet(gap, context));

        var definition = Query(block, null, entity, "timeline", context.Record);
        var config = (JsonObject)definition["config"]!;
        foreach (var key in new[] { "rowBy", "startField", "endField", "colorField", "labelField" })
            if (AppModel.Str(block[key]) is { } value) config[key] = value;
        if (block["axis"] is JsonObject axis) config["axis"] = axis.DeepClone();

        context.Imports.Add("TimelineBlock");
        source.Line("<TimelineBlock");
        source.Indent();
        source.Line($":definition=\"{Js(definition)}\"");
        source.Line($":state=\"{context.StateBinding}\"");
        if (context.Record) source.Line(":record=\"record\"");
        if (Echoes(AppModel.Str(block["label"]), context) is { Length: > 0 }) source.Line(":hide-title=\"true\"");
        source.Outdent();
        source.Line("/>");
    }

    /// <summary>The field keys a list shows, in order.</summary>
    private static IReadOnlyList<string> Columns(JsonObject block) =>
        [.. AppModel.Arr(block["fields"]).Select(AppModel.Str).OfType<string>()];

    /// <summary>A block's own query, in the shape a saved view has.</summary>
    private static JsonObject Query(
        JsonObject block, JsonObject? query, string? entity, string type, bool record)
    {
        var config = new JsonObject();
        var definition = new JsonObject
        {
            ["key"] = entity + "_" + type,
            ["type"] = type,
            ["entity"] = entity,
            ["config"] = config,
        };

        if (AppModel.Str(block["label"]) is { } label) definition["label"] = label;
        if (query?["filters"] is JsonArray filters) definition["filters"] = filters.DeepClone();
        if (query?["sort"] is JsonArray sort) definition["sort"] = sort.DeepClone();
        if (query?["limit"] is { } limit) definition["limit"] = limit.DeepClone();
        if (block["fields"] is JsonArray fields) config["columns"] = fields.DeepClone();

        // `via` is shorthand: the rows are the ones whose reference points at the record this screen
        // is about. Expanded into an ordinary filter leaf rather than given its own path through the
        // component, so it narrows the query the same way everything else does.
        if (record && AppModel.Str(query?["via"]) is { } via)
        {
            var narrowed = definition["filters"] as JsonArray;
            if (narrowed is null) definition["filters"] = narrowed = [];
            narrowed.Add(new JsonObject
            {
                ["field"] = via,
                ["operator"] = "eq",
                ["value"] = "{{record.id}}",
            });
        }

        return definition;
    }

    /// <summary>
    /// What this block asked for that the list components do not do.
    ///
    /// <para>One diagnostic per feature rather than one for the block, because "the table did not
    /// come out right" is not something anybody can act on and "inlineEdit is not emitted yet" is.
    /// </para>
    /// </summary>
    /// <param name="tabular">
    /// True where the caller renders through <c>ViewBlock</c>, which does manual row order and the
    /// quick-look panel. The calendar, board and timeline do neither — they have no rows to reorder,
    /// and they navigate on click — so for them these stay gaps, and saying nothing would be the
    /// silent kind.
    /// </param>
    private static IEnumerable<string> Gaps(
        JsonObject block, JsonObject? query, bool record, bool tabular = false)
    {
        if (query is not null)
        {
            foreach (var origin in new[] { "dates", "options", "platform" })
                if (query[origin] is not null)
                    yield return $"a list over '{origin}' rather than over an entity's records";

            if (query["via"] is not null && !record)
                yield return "a list bound with 'via' on a screen that is not about one record";
        }

        if (!tabular && block["orderField"] is not null)
            yield return "a list's manual row order ('orderField')";
        if (!tabular && AppModel.Bool(block["openDetail"]))
            yield return "a list's 'openDetail' panel overlay";
    }

    /// <summary>
    /// One thing the emitters have not got to, named precisely.
    ///
    /// <para>Separate from <see cref="Unrenderable"/>: that one answers "this whole block did not
    /// render", this one "the block rendered and one option on it did not". Both are CORD23xx —
    /// something a later release removes with no change to anybody's definition.</para>
    /// </summary>
    private static Diagnostic NotYet(string what, BlockContext context) =>
        new(NotYetCodes.BlockOption, $"the dotnet-vue generator does not emit {what} yet.", context.Path);

    /// <summary>A state-writing control: a value toggle, or a prev/next pair over a date.</summary>
    private static void Control(Source source, JsonObject block, BlockContext context)
    {
        var key = AppModel.Str(block["stateKey"]);
        var options = key is not null && context.State.TryGetValue(key, out var declared)
            ? declared["options"] as JsonArray
            : null;

        context.Imports.Add("ControlBlock");
        source.Line("<ControlBlock");
        source.Indent();
        source.Line($"control=\"{AppModel.Str(block["control"])}\"");
        source.Line($"state-key=\"{key}\"");
        source.Line($":state=\"{context.StateBinding}\"");
        if (AppModel.Str(block["label"]) is { } label) source.Line($"label={Quote(label)}");
        if (options is not null) source.Line($":options=\"{Js(options)}\"");
        if (block["step"] is JsonObject step) source.Line($":step=\"{Js(step)}\"");
        source.Outdent();
        source.Line("/>");
    }

    /// <summary>
    /// The same layout, once per record.
    ///
    /// <para>The children are emitted inside a scoped slot, so <c>record</c> inside a repeat is the
    /// REPEATED record rather than whatever the page was already showing. That is what makes the
    /// children ordinary blocks: a <c>field</c> in here and a <c>field</c> on a detail page are the
    /// same block reading the same name, and neither has to know which it is.</para>
    ///
    /// <para>Which means the children are emitted with a record context even on a page that has
    /// none — the one place on this target where those two things come apart.</para>
    /// </summary>
    /// <summary>
    /// A stat, from whichever of its two origins the author used.
    ///
    /// <para><b>They are not variations of one thing.</b> <c>source</c> aggregates a collection and
    /// costs a request; <c>field</c> reads a value off the record the block is bound to and costs
    /// nothing, because the row is already on the page. Only <c>source</c> was emitted, so a stat
    /// written the second way arrived at the component with NO origin at all — and since the
    /// component required one, it asked the server to aggregate <c>undefined</c>, failed, and
    /// rendered "unavailable". Every per-record figure in every generated application.</para>
    ///
    /// <para>What is still not emitted is reported rather than dropped. A stat that silently loses
    /// its denominator prints a numerator and calls it a percentage, which is worse than a build
    /// that says so.</para>
    /// </summary>
    /// <summary>
    /// One field of the bound record, WITH the presentation the definition asked for.
    ///
    /// <para>This emitted <c>&lt;RecordFields :columns="1"&gt;</c>, which is a form row: a grey
    /// caption above a value. So a card whose title is a <c>field</c> block rendered the word "Name"
    /// above the name, three times per card, and every card read as a half-filled form — while
    /// <c>size</c>, <c>weight</c>, <c>grow</c>, <c>icon</c> and <c>format</c> were dropped without a
    /// word, because a form row has nowhere to put them.</para>
    ///
    /// <para><c>kind: fields</c> still emits RecordFields. That block IS the form.</para>
    /// </summary>
    private static void Field(
        Source source, JsonObject block, BlockContext context, List<Diagnostic> unsupported)
    {
        if (RecordField(block["field"], "field", context, unsupported) is not { } field) return;

        context.Imports.Add("BlockField");

        var attributes = Attributes(
            ("field", field),
            ("label", AppModel.Str(block["label"])),
            ("size", AppModel.Str(block["size"])),
            ("weight", AppModel.Str(block["weight"])),
            ("color", AppModel.Str(block["color"])),
            ("icon", AppModel.Str(block["icon"])));

        if (AppModel.Bool(block["grow"])) attributes += " :grow=\"true\"";

        source.Line($"<BlockField entity=\"{context.Entity}\" :record=\"record\" {attributes} />");
    }

    /// <summary>
    /// A face, or the initials standing in for one.
    ///
    /// <para>There was no case for this at all, so every <c>kind: avatar</c> fell through to the
    /// default branch and drew the chip that says the build could not render it — once per card, in
    /// the exact spot the picture belonged.</para>
    /// </summary>
    private static void Avatar(
        Source source, JsonObject block, BlockContext context, List<Diagnostic> unsupported)
    {
        if (RecordField(block["field"], "avatar", context, unsupported) is not { } field) return;

        context.Imports.Add("BlockAvatar");
        source.Line($"<BlockAvatar entity=\"{context.Entity}\" :record=\"record\" "
            + $"{Attributes(("field", field), ("size", AppModel.Str(block["size"])))} />");
    }

    private static void Stat(
        Source source, JsonObject block, BlockContext context, List<Diagnostic> unsupported)
    {
        context.Imports.Add("StatCard");

        var attributes = new List<string>
        {
            Attributes(
                ("label", AppModel.Str(block["label"])),
                ("icon", AppModel.Str(block["icon"])),
                ("format", AppModel.Str(block["format"])),
                ("size", AppModel.Str(block["size"])),
                ("weight", AppModel.Str(block["weight"])),
                ("color", AppModel.Str(block["color"]))),
        };

        if (block["sources"] is not null || block["combine"] is not null)
        {
            unsupported.Add(new Diagnostic(NotYetCodes.BlockOption,
                "a 'stat' folding several sources with 'combine' is not emitted yet — "
                + "the figure will be missing rather than wrong.", context.Path));
        }

        if (RecordField(block["field"], "stat", context, unsupported) is { } field)
            attributes.Add($"field=\"{field}\" :record=\"record\"");

        if (Denominator(block["max"], "stat", context, unsupported) is { } max)
            attributes.Add(max);

        // `grow` defaults to true in the component, so only the author's `false` needs saying.
        if (block["grow"] is JsonValue grow && grow.GetValueKind() == JsonValueKind.False)
            attributes.Add(":grow=\"false\"");

        attributes.Add(Bind(("source", block["source"]), ("link", block["link"])));

        Self(source, "StatCard", string.Join(" ", attributes.Where(a => a.Length > 0)), "");
    }

    /// <summary>
    /// A progress bar.
    ///
    /// <para>The language makes <c>field</c> and <c>max</c> REQUIRED here, because a bar without a
    /// numerator and a denominator is not a bar. The emitter read neither and emitted a label — so
    /// a definition could ask for one, this target could claim to support it, the build could pass,
    /// and the screen showed "0 / " with nothing in it.</para>
    /// </summary>
    private static void Progress(
        Source source, JsonObject block, BlockContext context, List<Diagnostic> unsupported)
    {
        context.Imports.Add("ProgressBlock");

        var attributes = new List<string>
        {
            Attributes(("label", AppModel.Str(block["label"])), ("color", AppModel.Str(block["color"]))),
        };

        if (RecordField(block["field"], "progress", context, unsupported) is { } field)
            attributes.Add($"field=\"{field}\" :record=\"record\"");

        if (Denominator(block["max"] ?? block["target"], "progress", context, unsupported) is { } max)
            attributes.Add(max);

        attributes.Add(Bind(("source", block["source"])));

        Self(source, "ProgressBlock", string.Join(" ", attributes.Where(a => a.Length > 0)), "");
    }

    /// <summary>
    /// A block's <c>field</c>, when it can be read where the block sits.
    ///
    /// <para>Two things stop it, and both are reported rather than dropped. A field named on a page
    /// with no record in scope has nothing to read from. A field naming a hop through a reference —
    /// <c>shift.start_time</c> — needs the row on the OTHER side of that reference, which this
    /// block has not loaded; emitting the hop as a plain key would read <c>undefined</c> and print
    /// a dash on a screen that looks finished.</para>
    /// </summary>
    private static string? RecordField(
        JsonNode? node, string kind, BlockContext context, List<Diagnostic> unsupported)
    {
        if (AppModel.Str(node) is not { Length: > 0 } field) return null;

        if (!context.Record)
        {
            unsupported.Add(new Diagnostic(NotYetCodes.BlockOption,
                $"a '{kind}' block reads the field '{field}', but nothing on this screen binds a "
                + "record for it to read — put it inside a repeat or on a detail screen.",
                context.Path));
            return null;
        }

        if (field.Contains('.', StringComparison.Ordinal))
        {
            unsupported.Add(new Diagnostic(NotYetCodes.BlockOption,
                $"a '{kind}' block reading '{field}' hops through a reference, which this target "
                + "does not resolve yet — the figure will be missing rather than wrong.",
                context.Path));
            return null;
        }

        return field;
    }

    /// <summary>
    /// The denominator that turns a figure into a share, as the prop the component takes.
    ///
    /// <para>A number is bound; a sibling field key is an attribute, which is what the component
    /// looks up on the record. An aggregate over a whole collection — <c>{ source }</c> — is the
    /// one shape not emitted, and it is the one that changes the figure most, so it is
    /// reported.</para>
    /// </summary>
    private static string? Denominator(
        JsonNode? node, string kind, BlockContext context, List<Diagnostic> unsupported)
    {
        switch (node)
        {
            case null:
                return null;

            case JsonObject:
                unsupported.Add(new Diagnostic(NotYetCodes.BlockOption,
                    $"a '{kind}' block whose 'max' is an aggregate over a collection is not emitted "
                    + "yet — the share would be printed against no denominator.", context.Path));
                return null;

            case JsonValue value when value.GetValueKind() == JsonValueKind.Number:
                return $":max=\"{value.ToJsonString(Compact)}\"";

            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                return $"max=\"{value.GetValue<string>()}\"";

            default:
                return null;
        }
    }

    private static void Repeat(
        Source source, JsonObject block, BlockContext context, List<Diagnostic> unsupported)
    {
        context.Imports.Add("RepeatBlock");

        var entity = AppModel.Str(block["source"]?["entity"]) ?? context.Entity;

        // Both of these were dropped in silence, which is the one thing this target is not supposed
        // to do — a screen that quietly leaves something out looks finished.
        if (block["filterBar"] is not null)
            unsupported.Add(NotYet("a repeat's own filter bar (the list renders unfiltered)", context));

        if (AppModel.Str(block["as"]) is { } alias)
            unsupported.Add(NotYet(
                $"a repeat's '{alias}' scope name — a nested block naming '{alias}' will not resolve; "
                + "the repeated record is in scope as the block's own record", context));

        source.Line("<RepeatBlock");
        source.Indent();
        if (entity is not null) source.Line($"entity=\"{entity}\"");
        source.Line($":source=\"{Js(block["source"] ?? new JsonObject())}\"");
        if (AppModel.Str(block["emptyText"]) is { } empty) source.Line($"empty-text={Quote(empty)}");
        if (AppModel.Str(block["gap"]) is { } gap) source.Line($"gap=\"{gap}\"");

        // A card GRID rather than a stack. Dropped silently before this, so `wrap: true, cols: 3`
        // produced a column of page-wide cards and nothing said why.
        if (AppModel.Bool(block["wrap"])) source.Line(":wrap=\"true\"");
        if (block["cols"] is JsonValue cols && cols.GetValueKind() == JsonValueKind.Number)
            source.Line($":cols=\"{cols.ToJsonString(Compact)}\"");
        if (AppModel.Str(block["direction"]) is { } direction) source.Line($"direction=\"{direction}\"");

        // Whether the item itself opens the record. Worked out HERE rather than in the component,
        // because it is a question about the block tree and the tree is a build-time fact — the
        // platform's renderer answers the same question by walking the subtree on every render.
        if (entity is not null && !OwnsClickSurface(block["blocks"])) source.Line(":clickable=\"true\"");

        source.Line("v-slot=\"{ record }\"");
        source.Outdent();
        source.Line(">");
        source.Indent();

        // Inside the slot there IS a record, whatever was true outside it. Imports are shared with
        // the outer context rather than copied, so a component only the repeated children use is
        // still imported at the top of the page.
        var inner = context with { Entity = entity, Record = true };

        foreach (var child in AppModel.Arr(block["blocks"]).OfType<JsonObject>())
            Block(source, child, inner, unsupported);

        source.Outdent();
        source.Line("</RepeatBlock>");

        // `with` copies the mutable flag by value, so a conditional child inside the repeat would
        // otherwise not import the evaluator the page needs.
        if (inner.Conditional) context.Conditional = true;
    }

    /// <summary>
    /// Does anything in this subtree own the click?
    ///
    /// <para>A repeated card is worth clicking: it is one record, and opening it is the obvious
    /// thing to want. A repeated card containing a TABLE is not — the table's rows own their own
    /// clicks, and every miss beside a cell would navigate away from the row somebody was reading.
    /// So the rule is about what is drawn inside, and the answer is in the definition.</para>
    ///
    /// <para>Presentational leaves never disqualify an item. A field, a chip, an avatar, a progress
    /// bar and a stat are all things to read, and a card made of them is a card you click. Nor does
    /// an <c>action</c> button: "a row with an Approve button" still opens on click, and the button
    /// stops the event itself.</para>
    /// </summary>
    private static bool OwnsClickSurface(JsonNode? blocks)
    {
        foreach (var block in AppModel.Arr(blocks).OfType<JsonObject>())
        {
            if (ClickSurfaces.Contains(AppModel.Str(block["kind"]) ?? "")) return true;
            if (OwnsClickSurface(block["blocks"])) return true;

            foreach (var tab in AppModel.Arr(block["tabs"]).OfType<JsonObject>())
                if (OwnsClickSurface(tab["blocks"])) return true;

            foreach (var column in AppModel.Arr(block["columns"]))
                if (OwnsClickSurface(column)) return true;
        }

        return false;
    }

    private static readonly IReadOnlySet<string> ClickSurfaces =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "repeat", "cell", "view", "child", "settings", "table", "calendar", "board", "timeline",
            "split", "form", "hub", "process",
        };

    private static void Container(
        Source source, string component, string attributes, JsonNode? children,
        BlockContext context, List<Diagnostic> unsupported)
    {
        context.Imports.Add(component);

        var open = string.IsNullOrEmpty(attributes) ? $"<{component}>" : $"<{component} {attributes}>";
        source.Line(open);
        source.Indent();

        foreach (var child in AppModel.Arr(children).OfType<JsonObject>())
            Block(source, child, context, unsupported);

        source.Outdent();
        source.Line($"</{component}>");
    }

    private static void Columns(Source source, JsonObject block, BlockContext context, List<Diagnostic> unsupported)
    {
        context.Imports.Add("BlockColumns");
        context.Imports.Add("BlockColumn");

        var columns = AppModel.Arr(block["columns"]);

        source.Line("<BlockColumns>");
        source.Indent();

        foreach (var column in columns)
        {
            // The count travels with each column so the width is a number rather than "share the
            // row somehow" — see BlockColumn for what the somehow turned into.
            source.Line($"<BlockColumn :count=\"{columns.Count}\">");
            source.Indent();

            // A column is an array of blocks in the language; an object with its own `blocks` is the
            // other spelling the schema allows.
            var blocks = column is JsonArray array
                ? array.OfType<JsonObject>()
                : AppModel.Arr((column as JsonObject)?["blocks"]).OfType<JsonObject>();

            foreach (var child in blocks) Block(source, child, context, unsupported);

            source.Outdent();
            source.Line("</BlockColumn>");
        }

        source.Outdent();
        source.Line("</BlockColumns>");
    }

    private static void Tabs(Source source, JsonObject block, BlockContext context, List<Diagnostic> unsupported)
    {
        context.Imports.Add("BlockTabs");

        var tabs = AppModel.Arr(block["tabs"]).OfType<JsonObject>().ToList();
        var descriptors = new JsonArray([.. tabs.Select((t, i) => (JsonNode)new JsonObject
        {
            ["key"] = AppModel.Str(t["key"]) ?? $"tab{i}",
            ["label"] = AppModel.Str(t["label"]) ?? $"Tab {i + 1}",
        })]);

        source.Line($"<BlockTabs :tabs=\"{Js(descriptors)}\">");
        source.Indent();

        for (var i = 0; i < tabs.Count; i++)
        {
            var key = AppModel.Str(tabs[i]["key"]) ?? $"tab{i}";
            source.Line($"<template #{key}>");
            source.Indent();
            foreach (var child in AppModel.Arr(tabs[i]["blocks"]).OfType<JsonObject>())
                Block(source, child, context, unsupported);
            source.Outdent();
            source.Line("</template>");
        }

        source.Outdent();
        source.Line("</BlockTabs>");
    }

    private static void Self(Source source, string component, string attributes, string bindings)
    {
        var parts = new[] { attributes, bindings }.Where(p => !string.IsNullOrEmpty(p));
        source.Line($"<{component} {string.Join(" ", parts)} />");
    }

    /// <summary>
    /// <c> :hide-title="true"</c> when this block's own label would only repeat the page heading —
    /// nothing otherwise. Compared case-insensitively and trimmed, because "All tasks" and "All
    /// Tasks" are the same words to a reader.
    /// </summary>
    private static string Echoes(string? label, BlockContext context) =>
        label is { Length: > 0 } && context.PageTitle is { Length: > 0 } title
        && string.Equals(label.Trim(), title.Trim(), StringComparison.OrdinalIgnoreCase)
            ? " :hide-title=\"true\""
            : "";

    private static string Attributes(params (string Name, string? Value)[] pairs) =>
        string.Join(" ", pairs
            .Where(p => !string.IsNullOrEmpty(p.Value))
            .Select(p => $"{p.Name}={Quote(p.Value!)}"));

    /// <summary>A bound prop carrying a definition fragment verbatim — a stat's source, a chart's
    /// aggregate. Passed as data rather than unpacked into a dozen attributes, because these shapes
    /// are the definition's and the component reads them as such.</summary>
    private static string Bind(params (string Name, JsonNode? Value)[] pairs) =>
        string.Join(" ", pairs
            .Where(p => p.Value is not null)
            .Select(p => $":{p.Name}=\"{Js(p.Value!)}\""));

    /// <summary>JSON as a Vue attribute value. Double quotes delimit the attribute, so the JSON uses
    /// single ones — the same trick a hand-written template uses, and the reason nothing here needs
    /// HTML entity escaping.</summary>
    private static string Js(JsonNode node) =>
        node.ToJsonString(Compact).Replace("\"", "'", StringComparison.Ordinal);

    private static string JsArray(JsonNode? node) =>
        node is JsonArray array ? Js(array) : "[]";

    private static readonly JsonSerializerOptions Compact = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string Quote(string value) => "\"" + value.Replace("\"", "&quot;", StringComparison.Ordinal) + "\"";

    /// <summary>A JavaScript string literal, single-quoted so it survives being written inside a
    /// double-quoted Vue attribute as well as inside a script block.</summary>
    private static string JsString(string value) =>
        "'" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\'", StringComparison.Ordinal) + "'";

    /// <summary>
    /// Every route the application has, listed. A generated file rather than a scan, so the route
    /// table is something you can read.
    /// </summary>
    /// <remarks>
    /// <para><b>The shell's routes belong here too, and once did not.</b> This emitter listed the
    /// definition's screens and four of the shell's, leaving out access keys, the profile and the
    /// administration screen — while the shell's navigation linked to all of them. A
    /// <c>&lt;v-list-item :to="{ name: 'access-keys' }"&gt;</c> pointing at a name the router has
    /// never heard of does not render a dead link: <c>router.resolve</c> THROWS, from inside a
    /// render, and the page it was on goes with it. Every generated application had an uncaught
    /// error in its console from the moment somebody signed in.</para>
    ///
    /// <para>Keep this list and <c>Templates/web/src/router.js</c> saying the same thing. The
    /// template is the shape a scaffold starts in; this is the shape it is regenerated into, and a
    /// route in one and not the other is exactly the bug above.</para>
    /// </remarks>
    private static GeneratedFile Router(AppModel app)
    {
        var source = new Source(2);
        source.Line("import { createRouter, createWebHistory } from 'vue-router'");
        source.Line("import { session, loadSession } from './session.js'");
        source.Line();
        source.Line("import HomeView from './views/HomeView.vue'");
        source.Line("import DirectoryView from './views/DirectoryView.vue'");
        source.Line("import AdminUsersView from './views/AdminUsersView.vue'");
        source.Line("import ProfileView from './views/ProfileView.vue'");
        source.Line("import AccessKeysView from './views/AccessKeysView.vue'");
        source.Line("import LoginView from './views/LoginView.vue'");
        source.Line("import SetupView from './views/SetupView.vue'");
        if (app.Forms is not null) source.Line("import PublicFormView from './views/PublicFormView.vue'");

        foreach (var page in app.Pages)
            source.Line($"import {page.ComponentName} from './pages/{page.ComponentName}.vue'");

        foreach (var entity in app.Entities)
            source.Line($"import {entity.PascalKey}RecordPage from './pages/{entity.PascalKey}RecordPage.vue'");

        source.Line();
        source.Line("// The definition's screens, then the record pages behind them. Routes you add");
        source.Line("// yourself belong in a file of your own — regenerating replaces this one.");
        source.Line("const routes = [");
        source.Indent();
        source.Line("{ path: '/', name: 'home', component: HomeView },");
        source.Line("{ path: '/directory', name: 'directory', component: DirectoryView },");
        source.Line("{ path: '/admin/users', name: 'admin-users', component: AdminUsersView, meta: { administrator: true } },");
        source.Line("{ path: '/profile', name: 'profile', component: ProfileView },");
        source.Line("{ path: '/access-keys', name: 'access-keys', component: AccessKeysView },");
        source.Line("{ path: '/login', name: 'login', component: LoginView, meta: { anonymous: true } },");
        source.Line("{ path: '/setup', name: 'setup', component: SetupView, meta: { anonymous: true } },");
        // A published form, for somebody with no account. `anonymous` is what the guard reads; the
        // address is a generated TOKEN rather than a name anybody chose, because a memorable public
        // address is a guessable one.
        if (app.Forms is not null)
            source.Line("{ path: '/f/:token', name: 'public-form', component: PublicFormView, meta: { anonymous: true } },");

        foreach (var page in app.Pages)
            source.Line($"{{ path: '{page.Route}', name: '{page.Key}', component: {page.ComponentName} }},");

        foreach (var entity in app.Entities)
            source.Line($"{{ path: '/record/{entity.Key}/:id', name: '{entity.Key}_record', component: {entity.PascalKey}RecordPage }},");

        source.Outdent();
        source.Line("]");
        source.Line();
        source.Line("export const router = createRouter({");
        source.Line("  history: createWebHistory(),");
        source.Line("  routes,");
        source.Line("})");
        source.Line();
        source.Line("router.beforeEach(async (to) => {");
        source.Line("  // Asked once, on the first navigation. Every later route already knows.");
        source.Line("  if (!session.loaded) await loadSession()");
        source.Line();
        source.Line("  // A database with no administrator has exactly one thing anybody can do, so");
        source.Line("  // there is exactly one page to be on. Once one exists, setup is not a page.");
        source.Line("  if (session.setupRequired) return to.name === 'setup' ? true : { name: 'setup' }");
        source.Line("  if (to.name === 'setup') return session.authenticated ? { name: 'home' } : { name: 'login' }");
        source.Line();
        source.Line("  if (to.meta.anonymous) return true");
        source.Line();
        source.Line("  if (!session.authenticated) {");
        source.Line("    // `redirect` so that signing in returns to the page that was wanted. Landing");
        source.Line("    // everybody on the home page quietly loses the link somebody followed.");
        source.Line("    return { name: 'login', query: { redirect: to.fullPath } }");
        source.Line("  }");
        source.Line();
        source.Line("  // An account created by an administrator is on a password two people know. Nothing else");
        source.Line("  // in the application is reachable until that stops being true — a prompt somebody can");
        source.Line("  // dismiss is a prompt everybody dismisses.");
        source.Line("  if (session.mustChangePassword && to.name !== 'profile') return { name: 'profile' }");
        source.Line();
        source.Line("  // The server decides this too, and refuses the request either way. Checking it here only");
        source.Line("  // saves somebody the trip to a screen that would have come back empty.");
        source.Line("  if (to.meta.administrator && !session.isAdministrator) return { name: 'home' }");
        source.Line();
        source.Line("  return true");
        source.Line("})");

        return new GeneratedFile("web/src/router.js", source.ToString());
    }
}

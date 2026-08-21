// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cordango.SourceGen.DotNetVue.Model;

namespace Cordango.SourceGen.DotNetVue.Emit;

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
    public static WebResult Emit(AppModel app, bool allowPartial)
    {
        ArgumentNullException.ThrowIfNull(app);

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
        var context = new BlockContext(app, page.Entity, Record: false, $"$.pages[?(@.key=='{page.Key}')]");

        body.Indent().Indent();
        foreach (var block in page.Blocks.OfType<JsonObject>())
            Block(body, block, context, unsupported);

        return Component(
            path: $"web/src/pages/{page.ComponentName}.vue",
            imports: context.Imports,
            setup:
            [
                "// This screen came from the definition. Once you stop regenerating, it is an ordinary",
                "// Vue component and yours to change.",
            ],
            template: body.ToString(),
            title: page.Label,
            conditional: context.Conditional);
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

        var blocks = AppModel.Arr(entity.Detail?["blocks"]).OfType<JsonObject>().ToList();
        if (blocks.Count == 0)
        {
            // No detail authored. Every field, which is what somebody opening a record wants when
            // nobody has said otherwise — and better than an empty page.
            context.Imports.Add("RecordFields");
            body.Line($"<RecordFields entity=\"{entity.Key}\" :record=\"record\" />");
        }
        else
        {
            foreach (var block in blocks) Block(body, block, context, unsupported);
        }

        var name = entity.TypeName + "RecordPage";
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
        bool conditional = false)
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
        else if (conditional)
        {
            source.Line("import { visibleWhen } from '../records.js'");
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
    private sealed record BlockContext(AppModel App, string? Entity, bool Record, string Path)
    {
        public HashSet<string> Imports { get; } = Record ? [] : ["PageShell"];

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
            source.Line($"<template v-if=\"visibleWhen({Js(condition)}, {(context.Record ? "record" : "null")})\">");
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
                    Attributes(("label", AppModel.Str(block["label"]))), block["blocks"], context, unsupported);
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
                context.Imports.Add("BlockText");
                source.Line($"<BlockText {Attributes(("text", AppModel.Str(block["text"])), ("tone", AppModel.Str(block["tone"])))} />");
                break;

            case "stat":
                context.Imports.Add("StatCard");
                Self(source, "StatCard",
                    Attributes(("label", AppModel.Str(block["label"])), ("icon", AppModel.Str(block["icon"])), ("format", AppModel.Str(block["format"]))),
                    Bind(("source", block["source"]), ("link", block["link"])));
                break;

            case "chart":
                context.Imports.Add("ChartBlock");
                Self(source, "ChartBlock",
                    Attributes(("label", AppModel.Str(block["label"])), ("chartType", AppModel.Str(block["chartType"]))),
                    Bind(("source", block["source"])));
                break;

            case "progress":
                context.Imports.Add("ProgressBlock");
                Self(source, "ProgressBlock",
                    Attributes(("label", AppModel.Str(block["label"])), ("target", block["target"]?.ToString())),
                    Bind(("source", block["source"])));
                break;

            case "view":
                context.Imports.Add("ViewBlock");
                source.Line($"<ViewBlock view=\"{AppModel.Str(block["view"])}\" />");
                break;

            case "table":
                // A table naming its entity rather than a saved view. The compiler synthesizes a
                // view per entity, so there is always one to point at.
                context.Imports.Add("ViewBlock");
                source.Line($"<ViewBlock view=\"{AppModel.Str(block["entity"]) ?? context.Entity}_table\" />");
                break;

            case "child":
                context.Imports.Add("ChildBlock");
                source.Line($"<ChildBlock entity=\"{AppModel.Str(block["entity"])}\" field=\"{AppModel.Str(block["field"])}\" :record=\"record\" />");
                break;

            case "hub":
                context.Imports.Add("RecordHub");
                context.Imports.Add("RecordDialog");
                source.Line("<RecordHub");
                source.Indent();
                source.Line($"entity=\"{context.Entity}\"");
                source.Line(":record=\"record\"");
                source.Line($"title=\"{AppModel.Str(block["title"])}\"");
                source.Line($"status=\"{AppModel.Str(block["status"])}\"");
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
                context.Imports.Add("RecordFields");
                source.Line($"<RecordFields entity=\"{context.Entity}\" :record=\"record\" :fields=\"['{AppModel.Str(block["field"])}']\" :columns=\"1\" />");
                break;

            case "process":
                context.Imports.Add("RecordProcess");
                source.Line($"<RecordProcess entity=\"{context.Entity}\" :record=\"record\" />");
                break;

            default:
                context.Imports.Add("UnsupportedBlock");
                var reason = $"the dotnet-vue generator does not emit '{kind}' blocks yet.";
                source.Line($"<UnsupportedBlock kind=\"{kind}\" />");
                unsupported.Add(new Diagnostic("CORD2301", reason, context.Path));
                break;
        }
    }

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

    /// <summary>Every route the application has, listed. A generated file rather than a scan, so the
    /// route table is something you can read.</summary>
    private static GeneratedFile Router(AppModel app)
    {
        var source = new Source(2);
        source.Line("import { createRouter, createWebHistory } from 'vue-router'");
        source.Line("import { session, loadSession } from './session.js'");
        source.Line();
        source.Line("import HomeView from './views/HomeView.vue'");
        source.Line("import DirectoryView from './views/DirectoryView.vue'");
        source.Line("import LoginView from './views/LoginView.vue'");
        source.Line("import SetupView from './views/SetupView.vue'");

        foreach (var page in app.Pages)
            source.Line($"import {page.ComponentName} from './pages/{page.ComponentName}.vue'");

        foreach (var entity in app.Entities)
            source.Line($"import {entity.TypeName}RecordPage from './pages/{entity.TypeName}RecordPage.vue'");

        source.Line();
        source.Line("// The definition's screens, then the record pages behind them. Routes you add");
        source.Line("// yourself belong in a file of your own — regenerating replaces this one.");
        source.Line("const routes = [");
        source.Indent();
        source.Line("{ path: '/', name: 'home', component: HomeView },");
        source.Line("{ path: '/directory', name: 'directory', component: DirectoryView },");
        source.Line("{ path: '/login', name: 'login', component: LoginView, meta: { anonymous: true } },");
        source.Line("{ path: '/setup', name: 'setup', component: SetupView, meta: { anonymous: true } },");

        foreach (var page in app.Pages)
            source.Line($"{{ path: '{page.Route}', name: '{page.Key}', component: {page.ComponentName} }},");

        foreach (var entity in app.Entities)
            source.Line($"{{ path: '/record/{entity.Key}/:id', name: '{entity.Key}_record', component: {entity.TypeName}RecordPage }},");

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
        source.Line("  if (session.authenticated) return true");
        source.Line();
        source.Line("  // `redirect` so that signing in returns to the page that was wanted. Landing");
        source.Line("  // everybody on the home page quietly loses the link somebody followed.");
        source.Line("  return { name: 'login', query: { redirect: to.fullPath } }");
        source.Line("})");

        return new GeneratedFile("web/src/router.js", source.ToString());
    }
}

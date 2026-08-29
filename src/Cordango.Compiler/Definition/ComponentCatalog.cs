// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Definition;

/// <summary>
/// The typed catalog of UI/behavior components the AI may compose — the single source of truth
/// authored here in C# and <b>emitted as JSON</b> (see <see cref="Json"/>) for its three consumers:
/// the generator (injected into the prompt), the gate (validates each component's config +
/// preconditions), and the renderer (dispatches component id -> Vue component). This is to the
/// UI layer what the App Definition schema is to the data layer. See <c>architecture-component-catalog.md</c>.
///
/// Tiers (see <see cref="ComponentTier"/>): field renderers are governed by the App Definition's
/// <c>field.type</c> enum (surfaced via <see cref="FieldTypes"/>, not duplicated as entries); the
/// remaining tiers — views, widgets, blocks, capabilities — are full entries below.
/// </summary>
public static class ComponentCatalog
{
    /// <summary>Catalog contract version. 1.2: design pass (widget `type` discriminators + binding
    /// modes). 1.3: the north-star record components — block.hub, block.tiles — plus formal
    /// block.fields / block.child entries. 1.4: commands + processes — block.process stepper and the
    /// kanban `interaction` mode. 1.6: namespaced block families — every block-tier entry carries
    /// the `kind` the model writes (`layout.row`, `data.repeat`, `domain.hub`) and its `family`,
    /// derived from <see cref="BlockKinds"/>; `widgets` is marked deprecated. 1.7: four composable
    /// surfaces — block.table, block.calendar, block.form and block.split (persistent master-detail)
    /// — so a list, a month grid, an inline create form and a two-pane queue no longer require a
    /// named view or a side sheet. 1.8: table views take a `filterBar` (always-visible search +
    /// facet dropdowns, the shared $defs/filterBar shape). 1.9: table views take `inlineEdit`
    /// (in-place cell editing; governed statuses run their legal transitions as commands).</summary>
    public const string Version = "1.9";

    /// <summary>The full set of composable components (views, widgets, blocks, capabilities).</summary>
    public static readonly IReadOnlyList<ComponentDef> All = Build();

    /// <summary>Field-renderer ids = the App Definition <c>field.type</c> enum (single source).</summary>
    public static readonly IReadOnlyList<string> FieldTypes = LoadFieldTypes();

    /// <summary>Look up a component by id (e.g. "view.board"), or null.</summary>
    public static ComponentDef? Find(string id) => All.FirstOrDefault(c => c.Id == id);

    /// <summary>The binding modes a SCREEN CONTEXT offers. A page is bound to a collection; a
    /// detail is bound to one record; both can nest a repeat, whose children are bound to the
    /// current <c>item</c>. Used to narrow the per-slice tool schema — see
    /// <see cref="BlockIsLegalIn"/>.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> ContextBindings =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["page"] = ["collection", "item"],
            ["detail"] = ["record", "item"],
        };

    /// <summary>Whether a canonical block kind may appear in a screen context ("page" | "detail"),
    /// per its catalog <see cref="ComponentDef.Bindings"/>. A kind with no catalog entry, no declared
    /// bindings, or an unknown context is legal everywhere — narrowing may only ever drop what the
    /// catalog positively rules out.
    /// <para>Verified against the whole definition corpus (22 files): every block actually authored
    /// on a page is page-legal and every block on a detail is detail-legal under this rule, so
    /// narrowing removes only shapes nothing has ever used there.</para></summary>
    public static bool BlockIsLegalIn(string canonicalKind, string context)
    {
        if (!ContextBindings.TryGetValue(context, out var offered)) return true;
        var declared = Find("block." + canonicalKind)?.Bindings;
        if (declared is not { Length: > 0 }) return true;
        return declared.Any(b => offered.Contains(b, StringComparer.Ordinal));
    }

    /// <summary>The emitted JSON contract, built once. Shape: { version, fieldTypes, components:[…] }.</summary>
    public static string Json => LazyJson.Value;

    private static readonly Lazy<string> LazyJson = new(() => ToJson().ToJsonString());

    /// <summary>The catalog as a JsonObject (deep, mutable — callers own the copy).</summary>
    public static JsonObject ToJson()
    {
        var components = new JsonArray();
        foreach (var c in All) components.Add(ToJson(c));
        return new JsonObject
        {
            ["version"] = Version,
            ["fieldTypes"] = StrArray(FieldTypes),
            ["components"] = components,
        };
    }

    // ---- catalog entries -----------------------------------------------------------------------

    private static List<ComponentDef> Build() =>
    [
        // ============================ Tier: VIEW (a lens over one entity) ============================
        C("view.table", ComponentTier.View, "Table",
          "Sortable, filterable list of an entity's records with per-user column configuration.",
          "The default view for listing/scanning/filtering any entity. Use unless another view type's precondition is clearly met. Curate 5-8 meaningful columns — not every field.",
          """
          { "type":"object", "additionalProperties":false, "properties":{
            "columns":{"type":"array","minItems":1,"items":{"type":"string"},
              "description":"Field keys to show as columns, in order."},
            "defaultSort":{"type":"array","items":{"type":"object","additionalProperties":false,
              "required":["field","direction"],"properties":{
                "field":{"type":"string"},"direction":{"enum":["asc","desc"]}}}},
            "density":{"enum":["compact","comfortable"]},
            "allowDelete":{"type":"boolean","description":"Show the per-row delete action (default false)."},
            "newButton":{"type":"boolean","description":"Show the New button. DEFAULT TRUE — a list of records people work with is a list they add to. Set false where the rows arrive some other way and a New button would offer a record nobody should hand-make: an import log, a generated grid, a queue fed by a workflow."},
            "showFooter":{"type":"boolean","description":"Show the pagination footer even when the rows fit one page."},
            "filterBar":{"type":"object","additionalProperties":false,
              "description":"Always-visible search + facet dropdowns above the table — for lists long enough that finding a row matters. 'search' = fields the free-text box matches (references match their resolved names); 'facets' = select/reference fields rendered as filter dropdowns.",
              "properties":{
                "search":{"type":"array","minItems":1,"maxItems":6,"items":{"type":"string"}},
                "facets":{"type":"array","minItems":1,"maxItems":5,"items":{"type":"string"}}}},
            "inlineEdit":{"type":"boolean","description":"Edit cells in place (selects/dates/numbers/people; a governed status offers its legal transitions and runs the real command); the first column still opens the record."} } }
          """,
          bindings: ["collection"]),

        C("view.detail", ComponentTier.View, "Detail",
          "The full record view — field sections plus related lists, arrangeable into tabs. Author it via the entity's `detail` block tree, not as a view.",
          "Do NOT author detail views — compose each primary entity's record layout in the design document's `details` section instead.",
          """
          { "type":"object", "additionalProperties":false, "properties":{
            "blocks":{"type":"array","items":{"type":"object"}},
            "relatedLists":{"type":"array","items":{"type":"string"}} } }
          """,
          bindings: ["record"]),

        C("view.kanban", ComponentTier.View, "Board",
          "A board: columns are the discrete values of a grouping field (a select's options, or the records of a referenced entity); users drag cards between columns to CHANGE that field. Cards are compact record cards (cardFields).",
          "TWO shapes, same control. (1) PROCESS board — group by the role:'status' field of an entity a user moves through stages (lead->qualified->won; open->in_progress->closed): a drag runs the matching transition. (2) ASSIGNMENT board — group by a plain select/reference that says WHERE or TO WHOM a record belongs (station, office, store, shift, team, owner, room): a drag reassigns it. Staffing a shift by dragging people onto stations, or moving accounts between offices, IS a board. Do NOT use one for a purely descriptive attribute nobody reassigns (make, country, industry) — the test is whether dragging a card to another column is a real action a user takes.",
          """
          { "type":"object", "additionalProperties":false, "properties":{
            "groupByField":{"type":"string","description":"The select or reference field whose values become the columns. A process status (drag = transition), or an assignment field like station/office/owner (drag = reassign)."},
            "cardFields":{"type":"array","items":{"type":"string"}},
            "swimlaneField":{"type":"string"},
            "interaction":{"enum":["interactive","visualization"],"description":"interactive = users drag cards to change the grouping field (default); visualization = read-only columns (approval-shaped flows where moves happen via commands, not dragging)."} } }
          """,
          requires: Req(dataShape: [Shape("a 'select' or 'reference' field to group the columns by", fieldTypes: ["select", "reference"])]),
          bindings: ["collection"]),

        C("view.calendar", ComponentTier.View, "Calendar",
          "Records placed on a calendar by one of their date fields.",
          "When records are meaningfully anchored to a date/datetime (due dates, events, bookings) and a month/week grid aids planning.",
          // 'range' is the zoom the calendar OPENS at — the reader can always switch day/week/month/year.
          // 'day' rules an hour axis, so it needs a 'datetime' start; the gate enforces that rather
          // than letting a 'date' field produce one empty column.
          """
          { "type":"object", "additionalProperties":false, "properties":{
            "dateField":{"type":"string"},"endDateField":{"type":"string"},
            "titleField":{"type":"string"},"colorByField":{"type":"string"},
            "range":{"type":"string","enum":["day","week","month","year"]},
            "timeAxis":{"type":"object","additionalProperties":false,"properties":{
              "startHour":{"type":"integer"},"endHour":{"type":"integer"},"slotMinutes":{"type":"integer"} } } } }
          """,
          requires: Req(dataShape: [Shape("a 'date' or 'datetime' field", fieldTypes: ["date", "datetime"])]),
          bindings: ["collection"]),

        C("view.timeline", ComponentTier.View, "Timeline",
          "Records ordered along a time axis, optionally grouped into lanes.",
          "When sequence/progression over time is the point (project phases, history). Requires a date field.",
          """
          { "type":"object", "additionalProperties":false, "properties":{
            "dateField":{"type":"string"},"groupBy":{"type":"string"} } }
          """,
          requires: Req(dataShape: [Shape("a 'date' or 'datetime' field", fieldTypes: ["date", "datetime"])]),
          bindings: ["collection"]),

        C("view.dashboard", ComponentTier.View, "Dashboard",
          "A grid of widgets showing aggregate metrics and charts across an entity. Never edits records.",
          "For an at-a-glance overview of aggregates (counts, sums, trends). Compose it from widget.* components.",
          """
          { "type":"object", "additionalProperties":false, "properties":{
            "widgets":{"type":"array","items":{"type":"object"}},
            "layout":{"enum":["grid"]} } }
          """,
          bindings: ["collection"]),

        // ============================ Tier: WIDGET (a dashboard/home block over aggregated data) ====
        C("widget.metric", ComponentTier.Widget, "Metric",
          "A single KPI number (count / sum / avg) with an optional label and format.",
          "For a headline figure: total open tickets, sum of pipeline value, average resolution time.",
          """
          { "type":"object", "additionalProperties":false, "required":["type","source"],
            "$defs":{"aggSource":{"type":"object","additionalProperties":false,"required":["entity","aggregate"],
              "properties":{
                "entity":{"type":"string"},
                "aggregate":{"type":"object","additionalProperties":false,"required":["op"],"properties":{
                  "op":{"enum":["count","sum","avg","min","max"]},"field":{"type":"string"},"groupBy":{"type":"string"}}},
                "filters":{"type":"array","items":{"type":"object"}}}}},
            "properties":{
              "type":{"const":"metric"},
              "source":{"$ref":"#/$defs/aggSource"},
              "viz":{"type":"object","additionalProperties":false,"properties":{
                "title":{"type":"string"},"format":{"enum":["number","money","percent","duration"]}}} } }
          """,
          bindings: ["collection"]),

        C("widget.chart", ComponentTier.Widget, "Chart",
          "A bar / line / pie / donut / area chart over grouped aggregates.",
          "For distribution or trend over a grouping (records by status, sales by month). The aggregate MUST have a groupBy; prefix it 'month_of:' to bucket a date field by month (e.g. \"groupBy\":\"month_of:completed_at\").",
          """
          { "type":"object", "additionalProperties":false, "required":["type","source","viz"],
            "$defs":{"aggSource":{"type":"object","additionalProperties":false,"required":["entity","aggregate"],
              "properties":{
                "entity":{"type":"string"},
                "aggregate":{"type":"object","additionalProperties":false,"required":["op"],"properties":{
                  "op":{"enum":["count","sum","avg","min","max"]},"field":{"type":"string"},"groupBy":{"type":"string"}}},
                "filters":{"type":"array","items":{"type":"object"}}}}},
            "properties":{
              "type":{"const":"chart"},
              "source":{"$ref":"#/$defs/aggSource"},
              "viz":{"type":"object","additionalProperties":false,"required":["chartType"],"properties":{
                "chartType":{"enum":["bar","line","pie","donut","area"]},"title":{"type":"string"}}} } }
          """,
          bindings: ["collection"]),

        C("widget.list", ComponentTier.Widget, "List",
          "A compact top-N list of records (no aggregation) — e.g. most recent or highest-priority.",
          "For a short, sorted slice of records surfaced on a dashboard or home.",
          """
          { "type":"object", "additionalProperties":false, "required":["type","source"], "properties":{
            "type":{"const":"list"},
            "source":{"type":"object","additionalProperties":false,"required":["entity"],"properties":{
              "entity":{"type":"string"},"sort":{"type":"array","items":{"type":"object"}},
              "limit":{"type":"integer","minimum":1},"filters":{"type":"array","items":{"type":"object"}}}},
            "viz":{"type":"object","additionalProperties":false,"properties":{
              "title":{"type":"string"},"fields":{"type":"array","items":{"type":"string"}}}} } }
          """,
          bindings: ["collection"]),

        // ============================ Tier: BLOCK (compose views/widgets onto a page) ================
        C("block.tabs", ComponentTier.Block, "Tabs",
          "A tab strip; each tab hosts its own set of blocks (an 'Overview' tab of widgets, an 'All records' tab with a table, a 'Board' tab, …).",
          "To present several lenses of the same entity (or a record) under one page without cluttering navigation.",
          """
          { "type":"object", "additionalProperties":false, "required":["tabs"], "properties":{
            "tabs":{"type":"array","minItems":1,"items":{"type":"object","additionalProperties":false,
              "required":["label","blocks"],"properties":{
                "label":{"type":"string"},"blocks":{"type":"array","items":{"type":"object"}}}}} } }
          """,
          slots: ["view", "widget", "block"], bindings: ["collection", "record"]),

        C("block.section", ComponentTier.Block, "Section",
          "A labeled group of blocks stacked vertically.",
          "To title and group related content within a page or tab.",
          """
          { "type":"object", "additionalProperties":false, "required":["blocks"], "properties":{
            "label":{"type":"string"},"blocks":{"type":"array","items":{"type":"object"}} } }
          """,
          slots: ["view", "widget", "block"], bindings: ["collection", "record"]),

        C("block.columns", ComponentTier.Block, "Columns",
          "Side-by-side columns, each hosting its own blocks.",
          "For two/three-up layouts (a form beside a summary; charts side by side).",
          """
          { "type":"object", "additionalProperties":false, "required":["columns"], "properties":{
            "columns":{"type":"array","minItems":2,"items":{"type":"array","items":{"type":"object"}}} } }
          """,
          slots: ["view", "widget", "block"], bindings: ["collection", "record"]),

        C("block.view", ComponentTier.Block, "Embedded view",
          "Embeds a named view (by key) as a block on a page.",
          "The leaf that places a table/board/calendar/etc. onto a composed page or tab.",
          """
          { "type":"object", "additionalProperties":false, "required":["view"], "properties":{
            "view":{"type":"string","description":"A view key defined in views[]."} } }
          """,
          bindings: ["collection"]),

        C("block.widgets", ComponentTier.Block, "Widget grid",
          "An inline grid of uniform KPI/chart widgets on a page.",
          "FALLBACK ONLY — it renders the same generic dashboard for every domain, which is exactly why apps end up looking identical. PREFER composing a home from primitives (card/row/repeat/stat/chart/field) so a hackathon gets a leaderboard, a CRM a funnel, a support desk a work-queue. Reach for a widgets grid only when a plain uniform KPI strip genuinely IS the right answer.",
          """
          { "type":"object", "additionalProperties":false, "required":["widgets"], "properties":{
            "widgets":{"type":"array","items":{"type":"object"}},"layout":{"enum":["grid"]} } }
          """,
          bindings: ["collection"]),

        C("block.hub", ComponentTier.Block, "Subject hub header",
          "The record's identity header: avatar/initials, title, status chip, a subtitle line, a compact key-facts row, and the record actions.",
          "FIRST block of a subject-like entity's detail — people, assets, cases, candidates, customers: records with an identity a user 'visits'. Title defaults to displayField, status to the role:'status' field; person-reference facts render as person chips.",
          """
          { "type":"object", "additionalProperties":false, "properties":{
            "title":{"type":"string","description":"Field key for the headline. Defaults to displayField."},
            "status":{"type":"string","description":"Select field for the status chip. Defaults to the role:'status' field."},
            "subtitle":{"type":"array","items":{"type":"string"},"description":"Field keys for the secondary line."},
            "facts":{"type":"array","items":{"type":"string"},"description":"4-6 field keys for the key-facts row."},
            "avatar":{"type":"object","additionalProperties":false,"properties":{"field":{"type":"string"}}},
            "actions":{"type":"array","items":{"enum":["edit","delete"]}} } }
          """,
          bindings: ["record"]),

        C("block.tiles", ComponentTier.Block, "Status tiles",
          "A KPI tile row: each tile shows a value with optional progress bar (value/max) and an attention chip when a threshold rule fires.",
          "Right under a hub header to read the record's state at a glance (record binding: tiles over the record's own fields or its children via aggregate sources), or on a page as a compact KPI strip (collection binding: source tiles only).",
          """
          { "type":"object", "additionalProperties":false, "required":["tiles"], "properties":{
            "tiles":{"type":"array","minItems":1,"items":{"type":"object","additionalProperties":false,
              "required":["label"],"properties":{
                "label":{"type":"string"},"icon":{"type":"string"},
                "format":{"enum":["number","money","percent"]},
                "field":{"type":"string","description":"Record binding: read the value from this field of the bound record."},
                "source":{"type":"object","description":"Aggregate source { entity, via?, aggregate:{op,field?}, filters? }; via = the child's reference field pointing at the bound record."},
                "max":{"description":"Progress denominator: a sibling field key, a number, or { source }."},
                "attention":{"type":"object","description":"{ op: eq|neq|gt|gte|lt|lte, value } — warning chip when true of the computed value."} }}} } }
          """,
          bindings: ["collection", "record"]),

        C("block.fields", ComponentTier.Block, "Field grid",
          "A label/value grid over a subset of the bound record's fields (person references render as chips).",
          "The detail workhorse: an Overview block with the identifying fields, or one block per field group inside tabs/sections. Omit `fields` to render all declared fields.",
          """
          { "type":"object", "additionalProperties":false, "properties":{
            "fields":{"type":"array","items":{"type":"string"}},
            "cols":{"type":"integer","minimum":1,"maximum":6},
            "hideEmpty":{"type":"boolean","description":"Hide blank values so optional profile sections show only known information."},
            "emptyText":{"type":"string","description":"Shown when hideEmpty removes every field."} } }
          """,
          bindings: ["record"]),

        C("block.child", ComponentTier.Block, "Child records",
          "A child entity's records embedded in the parent record's detail: comment feed, checklist, line-items grid, or inline table — with inline create.",
          "For every subordinate (ownedBy) entity, inside the parent's detail. childType: feed (comment-like), checklist (checkable steps), lineItems (money/quantity rows), table (default).",
          """
          { "type":"object", "additionalProperties":false, "required":["entity","via"], "properties":{
            "entity":{"type":"string"},"via":{"type":"string","description":"The reference field on the child pointing at the bound record's entity."},
            "childType":{"enum":["feed","checklist","lineItems","cards","table"]},
            "readOnly":{"type":"boolean","description":"A list nobody types (research runs, an import log): no Add, no row pencil, no delete; a row opens read-only."},
            "subordinate":{"type":"boolean"},"label":{"type":"string"} } }
          """,
          bindings: ["record"]),

        C("block.process", ComponentTier.Block, "Process stepper",
          "The record's state stepper: the process states as a progress rail with the current state highlighted, plus a button per available transition (its bound command, guarded by state + permission).",
          "On the detail of an entity governed by a process (an app declaring a processes[] entry for it). Place it right after the hub. Everything derives from the entity's process — no config.",
          """
          { "type":"object", "additionalProperties":false, "properties":{
            "label":{"type":"string"} } }
          """,
          bindings: ["record"]),

        C("block.answers", ComponentTier.Block, "Submitted answers",
          "The submission's questions and answers, in the form's order. Silent for a record created by hand.",
          "Near the TOP of the detail of an entity an intake form creates (ticket, application) — the answers ARE the request. Needs a reference from this entity to the role:'formResponse' entity.",
          """
          { "type":"object", "additionalProperties":false, "properties":{
            "label":{"type":"string"},
            "via":{"type":"string","description":"Reference field on THIS entity pointing at the formResponse entity. Omit when there is only one."} } }
          """,
          requires: Req(module: "forms"), bindings: ["record"]),

        C("block.intake", ComponentTier.Block, "File a request",
          "The forms someone may fill in, and the chosen form. Submitting needs only the right to file a response — the platform creates the routed record for the submitter.",
          "A 'file a request' page where forms create records: what lets a requester who may NOT create tickets raise one. Filter to the active forms.",
          """
          { "type":"object", "additionalProperties":false, "properties":{
            "label":{"type":"string"},
            "entity":{"type":"string","description":"Entity key of the role:'formTemplate' entity. Omit when there is only one."},
            "filters":{"type":"array","description":"Which forms to offer — typically the active ones."} } }
          """,
          requires: Req(module: "forms"), bindings: ["collection"]),

        // ================ Tier: BLOCK — composable PRIMITIVES (M2) ================
        // Generic, data-bindable controls you ARRANGE into distinctive surfaces (leaderboards, timelines,
        // funnels, feeds) instead of the fixed hub+tiles+tabs skeleton. Templates (hub/tiles/table/widgets)
        // stay for the common case; reach for primitives when a domain deserves a bespoke surface — this
        // is how two domains stop looking identical. Bindings: 'collection' (a page), 'record' (a detail),
        // 'item' (inside a repeat, the current row).
        C("block.card", ComponentTier.Block, "Card",
          "A titled surface wrapping a vertical stack of blocks.",
          "Group a composed surface (a leaderboard, a stat cluster) under a heading.",
          """
          { "type":"object","additionalProperties":false,"required":["blocks"],"properties":{
            "label":{"type":"string","description":"The card's heading."},"icon":{"type":"string"},
            "blocks":{"type":"array","items":{"type":"object"}} } }
          """,
          slots: ["block"], bindings: ["collection", "record", "item"]),

        // TEXTURE (width/padding/tone/bordered/minHeight) is not decoration: without it every container
        // has exactly one look, so composing a distinctive surface means composing identical-looking
        // atoms — and a grid CELL is undrawable (it needs a shared column width, its own padding, and a
        // minimum height so an empty cell still holds its slot).
        C("block.row", ComponentTier.Block, "Row",
          "Lay child blocks out horizontally — a leaderboard line: rank · avatar · name · number.",
          "Any left-to-right arrangement of primitives; set a child's `grow:true` to fill the space.",
          """
          { "type":"object","additionalProperties":false,"required":["blocks"],"properties":{
            "blocks":{"type":"array","items":{"type":"object"}},"align":{"enum":["start","center","end","baseline"]},
            "gap":{"enum":["none","sm","md","lg"]},"wrap":{"type":"boolean"},
            "width":{"description":"'xs'|'sm'|'md'|'lg', a pixel number, or a CSS length. Every column of a grid — header strip and body cells — must share ONE width or they drift apart."},
            "padding":{"enum":["none","xs","sm","md","lg"]},"tone":{"enum":["muted","accent","warn","danger"]},
            "bordered":{"type":"boolean","description":"Outline the box — the gridlines of a composed grid."},
            "minHeight":{"description":"A pixel number or CSS length. An empty grid cell must still hold its slot."} } }
          """,
          slots: ["block"], bindings: ["collection", "record", "item"]),

        C("block.stack", ComponentTier.Block, "Stack",
          "Lay child blocks out vertically (a two-line cell: title over subtitle).",
          "Vertical grouping of primitives without a card surface. With `width`/`padding`/`bordered`/`minHeight` this is also the CELL of a composed grid.",
          """
          { "type":"object","additionalProperties":false,"required":["blocks"],"properties":{
            "blocks":{"type":"array","items":{"type":"object"}},"gap":{"enum":["none","sm","md","lg"]},
            "width":{"description":"'xs'|'sm'|'md'|'lg', a pixel number, or a CSS length. Every column of a grid — header strip and body cells — must share ONE width or they drift apart."},
            "padding":{"enum":["none","xs","sm","md","lg"]},"tone":{"enum":["muted","accent","warn","danger"]},
            "bordered":{"type":"boolean","description":"Outline the box — the gridlines of a composed grid."},
            "minHeight":{"description":"A pixel number or CSS length. An empty grid cell must still hold its slot."} } }
          """,
          slots: ["block"], bindings: ["collection", "record", "item"]),

        C("block.grid", ComponentTier.Block, "Grid",
          "Lay child blocks out in an N-column grid.",
          "A card grid / gallery of repeated items, or a multi-column stat cluster.",
          """
          { "type":"object","additionalProperties":false,"required":["blocks"],"properties":{
            "blocks":{"type":"array","items":{"type":"object"}},"cols":{"type":"integer","minimum":1,"maximum":6},
            "gap":{"enum":["none","sm","md","lg"]} } }
          """,
          slots: ["block"], bindings: ["collection", "record", "item"]),

        C("block.repeat", ComponentTier.Block, "Repeat",
          "The engine of every composed surface: loads its source items and renders its child blocks ONCE PER ITEM, binding each to the current item. Its source is ONE of four origins — an entity's rows, a DATE RANGE, a select field's OPTIONS, or the platform directory. The last three iterate things that are not records, and that is what makes a SECOND AXIS possible: a board's columns are Mon..Sun, which live in no table. `as:'<name>'` publishes the current item to every descendant, so a deeply nested cell can read BOTH axes at once ({{row.id}} AND {{col.date}}) instead of only its immediate parent. `direction:'row'` lays the items out as columns. `via` scopes to the bound record's children.",
          "A leaderboard (sort desc, limit 10), a timeline, a funnel over a select's options, a work-queue, any feed — AND any 2-D surface: nest a repeat inside a repeat, name both axes, and have the innermost repeat filter on both to fill a cell. A roster/planner/shift board is repeats over rows × days, NOT a special component — none exists. Children are primitives that read the item: field/stat/chip/avatar/progress/action.",
          """
          { "type":"object","additionalProperties":false,"required":["source","blocks"],"properties":{
            "source":{"type":"object","description":"EXACTLY ONE origin: { entity } records | { dates:{from,to?,step?,count?} } a date axis (items: {id,date,next,label,weekday,isWeekend,isToday} — `next` is the START OF THE NEXT BUCKET, so matching a datetime to a day is `gte {{day.date}}` AND `lt {{day.next}}`; two filters against the same value match nothing) | { options:{entity,field} } a select's choices (items: {id,value,label,color}) | { platform:'person'|'department'|'group' } the tenant directory. Plus, on an entity source: filters?:[{field|path,operator,value}], sort?:[{field,direction}], limit?, via?. A filter `value` may be a scope token: {{actor.id}}, {{today}}, {{<name>.<field>}}. A filter `path` hops one relation ('shift.shift_date')."},
            "as":{"type":"string","description":"Publish the current item under this name for ALL descendants — '{{staff.id}}', '{{day.date}}'. Required to build a grid: without it a cell cannot know its column."},
            "direction":{"enum":["row","column"],"description":"'row' lays the items out left-to-right — the columns of a board."},
            "gap":{"enum":["none","sm","md","lg"]},
            "emptyText":{"type":"string","description":"Shown when there are no items. Use \"\" for a grid cell."},
            "blocks":{"type":"array","items":{"type":"object"}} } }
          """,
          slots: ["block"], bindings: ["collection", "record", "item"]),

        C("block.cell", ComponentTier.Block, "Cell (editable)",
          "THE ONE RECORD identified by `keys` — normally the two axes of the grid the cell sits at ({\"person\":\"{{row.id}}\",\"competency\":\"{{col.id}}\"}). Unlike a repeat that filters down to one row, an ABSENT record is still a real cell: with `editable:true` the value is changed in place, and writing an empty cell CREATES the record with those keys already set.",
          "Use it whenever THE GRID IS THE WORK. People do not rate one competency at a time, or fill a roster one dialog at a time — they open the matrix and fill it in. A read-only grid of joins plus a 'New' button that re-asks for the row and column the grid already knows is the single most common way a generated app ends up factually correct and unusable. Cell = the fastest path from 'I can see it' to 'I can do it'.",
          """
          { "type":"object","additionalProperties":false,"required":["entity","keys","field"],"properties":{
            "entity":{"type":"string","description":"The entity this cell reads/writes — usually the join entity the grid renders."},
            "keys":{"type":"object","description":"Field -> value identifying the record, normally scope tokens: {\"person\":\"{{row.id}}\",\"competency\":\"{{col.id}}\"}. Every key must be a field of `entity`. These are what a created record is stamped with."},
            "field":{"type":"string","description":"The field to show/edit. Cannot also be one of the keys."},
            "editable":{"type":"boolean","description":"Write through on change, creating the record if absent. Refused on system/readOnly/auto fields and on a field a process governs (those move via transitions)."},
            "placeholder":{"type":"string","description":"What an empty cell shows. Keep it quiet."} } }
          """,
          bindings: ["collection", "record", "item"]),

        C("block.table", ComponentTier.Block, "Table",
          "A source's records as a real data table (sort, per-column filter, column config, export) — the table VIEW's engine, but fed by a blockSource, so it filters against the scope chain and drops anywhere in a block tree without declaring a named view.",
          "The default way to show a list INSIDE a composed surface: a filtered queue on a home page, a related list scoped with {{record.id}}, a 'my open items' table. Use a named table view only when the page IS that one list. ENTITY source only.",
          """
          { "type":"object","additionalProperties":false,"required":["source"],"properties":{
            "source":{"type":"object","description":"An ENTITY origin: { entity, filters?, sort?, limit?, via? }. A dates/options/platform axis has no columns to tabulate."},
            "fields":{"type":"array","items":{"type":"string"},"description":"The COLUMNS, in order. Omit for all declared fields — curate 5-8 that help FIND a record."},
            "openDetail":{"type":"boolean","description":"Open a clicked row in the quick-look panel instead of the full-screen detail."},
            "tree":{"type":"boolean","description":"Nest rows by the entity's self-reference (a company's parent company); the parent field is derived and drops as a column."},
            "newButton":{"type":"boolean","description":"Show a 'New <entity>' button above the list. Default FALSE — a page whose only list is a table block cannot create anything unless you set this."},
            "label":{"type":"string"} } }
          """,
          bindings: ["collection", "record", "item"]),

        C("block.calendar", ComponentTier.Block, "Calendar",
          "A source's records on a MONTH GRID, placed by a date field and coloured by a select's option colours. Composable counterpart of the calendar view: its rows come from a blockSource, so it can be scoped to the bound record or the signed-in user.",
          "When 'what falls on which day' is part of a bigger surface — a team's absences beside their balances, a room's bookings inside its detail. For multi-day BARS across lanes use timeline instead; a calendar places single dates.",
          """
          { "type":"object","additionalProperties":false,"required":["source","startField"],"properties":{
            "source":{"type":"object","description":"An ENTITY origin: { entity, filters?, sort?, limit?, via? }."},
            "startField":{"type":"string","description":"The date/datetime field that places a record on the grid."},
            "labelField":{"type":"string","description":"Field labelling each entry (defaults to the entity's displayField)."},
            "colorField":{"type":"string","description":"A select field whose option colour fills each entry."},
            "label":{"type":"string"} } }
          """,
          bindings: ["collection", "record", "item"]),

        C("block.form", ComponentTier.Block, "Inline form",
          "An entity's CREATE form rendered inline — reusing that entity's authored `form` layout and validation — instead of being reachable only behind a New button in a side sheet.",
          "When submitting IS the screen's job: an intake/request page, a quick-capture box under a queue. Inside a record detail add `via` so what you create is linked to the record on screen. Not a substitute for the normal New action on an ordinary collection page.",
          """
          { "type":"object","additionalProperties":false,"required":["entity"],"properties":{
            "entity":{"type":"string","description":"The entity whose create form renders here."},
            "via":{"type":"string","description":"In a record detail: the reference field on `entity` stamped with the bound record's id."},
            "label":{"type":"string"} } }
          """,
          bindings: ["collection", "record", "item"]),

        C("block.split", ComponentTier.Block, "Master-detail split",
          "A PERSISTENT two-pane surface: the left pane lists the source's records, `blocks` render for the SELECTED row under the same item binding a repeat gives its children. The list stays on screen and the selection persists.",
          "The shape a triage/review/inbox wants — work THROUGH a list without losing it. Distinct from clicking a row into the full-screen detail (leaves the list) and from the quick-look panel (transient).",
          """
          { "type":"object","additionalProperties":false,"required":["source","blocks"],"properties":{
            "source":{"type":"object","description":"An ENTITY origin for the LIST pane: { entity, filters?, sort?, limit?, via? }."},
            "fields":{"type":"array","items":{"type":"string"},"description":"Secondary lines under each list row (the title is the displayField)."},
            "as":{"type":"string","description":"Publish the selected record under this name for the detail pane's descendants."},
            "blocks":{"type":"array","items":{"type":"object"},"description":"The DETAIL pane, bound to the selected row."},
            "label":{"type":"string"},"emptyText":{"type":"string"} } }
          """,
          slots: ["block"], bindings: ["collection", "record", "item"]),

        C("block.field", ComponentTier.Block, "Field value",
          "Renders ONE field value of the current row/record, formatted (dates, money, select labels, person chips). `field` takes one relation hop — 'shift.start_time' reads the start time of the shift this assignment points at. Distinct from the multi-field 'fields' grid.",
          "Inside a repeat row or a record detail, to show a single value. Use the hop when the fact you want lives on the referenced record — a cell that selected the right rows but can only print their raw ids is useless. `grow:true` lets it fill a row.",
          """
          { "type":"object","additionalProperties":false,"required":["field"],"properties":{
            "field":{"type":"string","description":"A field key, or ONE hop through a reference ('shift.start_time'). The hop must land on a plain value, not on another reference."},
            "label":{"type":"string"},
            "format":{"enum":["number","money","percent","date","datetime"]},"grow":{"type":"boolean"} } }
          """,
          bindings: ["record", "item"]),

        C("block.stat", ComponentTier.Block, "Stat",
          "One big number: a field of the current row/record, OR an aggregate (count/sum/avg/min/max) over an entity.",
          "A spotlight KPI on a home, or a per-row number in a repeat. `source` aggregates; `field` reads a value.",
          """
          { "type":"object","additionalProperties":false,"properties":{
            "field":{"type":"string"},
            "source":{"type":"object","description":"{ entity, aggregate:{op,field?}, filters?, via? }"},
            "label":{"type":"string"},"format":{"enum":["number","money","percent"]},"icon":{"type":"string"} } }
          """,
          bindings: ["collection", "record", "item"]),

        C("block.text", ComponentTier.Block, "Text",
          "A text/heading string. Interpolates {fieldKey} and #{index} against the current row, AND {{scope.path}} against the scope chain — '{{day.label}}' is how a column header prints the axis item it belongs to.",
          "Labels, headings, a rank badge, a grid's column headers, or a sentence composed from row fields.",
          """
          { "type":"object","additionalProperties":false,"properties":{
            "value":{"type":"string"},"size":{"enum":["xs","sm","md","lg","xl"]},
            "weight":{"enum":["normal","medium","bold"]},"color":{"type":"string"} } }
          """,
          bindings: ["collection", "record", "item"]),

        C("block.chip", ComponentTier.Block, "Chip",
          "A colored pill for a select value (uses the option's color) or a literal value.",
          "Show a status/category inline in a repeat row or a record surface.",
          """
          { "type":"object","additionalProperties":false,"properties":{
            "field":{"type":"string"},"value":{},"color":{"type":"string"} } }
          """,
          bindings: ["record", "item"]),

        C("block.avatar", ComponentTier.Block, "Avatar",
          "An identity avatar from a field — a person chip for person references, else colored initials.",
          "Lead a leaderboard/feed row with the person or subject it is about.",
          """
          { "type":"object","additionalProperties":false,"required":["field"],"properties":{
            "field":{"type":"string"} } }
          """,
          bindings: ["record", "item"]),

        C("block.progress", ComponentTier.Block, "Progress",
          "A progress bar: a field value over a max (a sibling field key or a number).",
          "A funnel stage (count/total), a completion meter, a quota bar — inside a repeat row or a detail.",
          """
          { "type":"object","additionalProperties":false,"required":["field","max"],"properties":{
            "field":{"type":"string"},"max":{"description":"a sibling field key or a number"},"label":{"type":"string"} } }
          """,
          bindings: ["record", "item"]),

        C("block.chart", ComponentTier.Block, "Chart (primitive)",
          "A single bar/line/pie/donut/area over an entity grouped by a select/reference/date field.",
          "A distribution or trend composed onto a home or detail — the primitive form of a chart widget.",
          """
          { "type":"object","additionalProperties":false,"required":["source","chartType"],"properties":{
            "source":{"type":"object","description":"{ entity, aggregate:{op,groupBy}, filters? }"},
            "chartType":{"enum":["bar","line","pie","donut","area"]},"label":{"type":"string","description":"Chart heading."} } }
          """,
          bindings: ["collection", "record"]),

        C("block.action", ComponentTier.Block, "Action button",
          "A button bound to a command defined on the current row/record's entity.",
          "A row-level action in a work-queue (Call, Approve) or a record action composed inline.",
          """
          { "type":"object","additionalProperties":false,"required":["command"],"properties":{
            "command":{"type":"string"},"style":{"enum":["primary","default","danger"]} } }
          """,
          bindings: ["record", "item"]),

        C("block.create", ComponentTier.Block, "New record button",
          "A 'New <record>' button opening the entity's create form.",
          "Where people create records but the surface cannot draw its own New button — a composed "
          + "grid, a calendar, a card wall, a toolbar. A table/board sets newButton instead.",
          """
          { "type":"object","additionalProperties":false,"required":["entity"],"properties":{
            "entity":{"type":"string"},"label":{"type":"string"},
            "style":{"enum":["primary","default"]},"icon":{"type":"string"} } }
          """,
          bindings: ["collection"]),

        // ============================ Tier: CAPABILITY (effectful; module-gated) =====================
        C("capability.ai_assistant", ComponentTier.Capability, "AI Assistant",
          "An in-app panel/action that uses AI over the app's own data — ask, summarize, classify, or extract.",
          "When the app itself should USE AI (Q&A over records, auto-summaries, classify/route). Declares which entity/fields it may read.",
          """
          { "type":"object", "additionalProperties":false, "required":["mode"], "properties":{
            "mode":{"enum":["ask","summarize","classify","extract"]},
            "scope":{"type":"object","additionalProperties":false,"properties":{
              "entity":{"type":"string"},"fields":{"type":"array","items":{"type":"string"}}}},
            "instructions":{"type":"string"},
            "output":{"type":"object"} } }
          """,
          requires: Req(module: "ai")),

        C("capability.api_integration", ComponentTier.Capability, "API / Integration",
          "Fetches from or pushes to an external system via a configured connector (no inline credentials).",
          "When the app must read/write external data (an external CRM, accounting, a webhook). Credentials live in the control plane; the component references a connection.",
          """
          { "type":"object", "additionalProperties":false, "required":["direction","connectionRef"], "properties":{
            "direction":{"enum":["fetch","push"]},
            "connectionRef":{"type":"string","description":"Id of a control-plane connection/credential."},
            "request":{"type":"object","additionalProperties":false,"properties":{
              "method":{"enum":["GET","POST","PUT","PATCH","DELETE"]},"path":{"type":"string"},
              "bodyMapping":{"type":"object"}}},
            "resultMapping":{"type":"object"} } }
          """,
          requires: Req(module: "connectors")),

        C("capability.action", ComponentTier.Capability, "Action",
          "A button that runs typed effects on a record. Prefer top-level commands[] (with placements, guards, input, confirmation and process binding) — this catalog entry is the legacy inline form.",
          "For user-initiated effects. Distinct from views: it DOES something rather than displaying data. New apps should model these as commands[].",
          """
          { "type":"object", "additionalProperties":false, "required":["label","action"], "properties":{
            "label":{"type":"string"},
            "action":{"type":"object","additionalProperties":false,"required":["type"],"properties":{
              "type":{"enum":["updateRecord","createRecord","notify","email","webhook"]},
              "params":{"type":"object"}}},
            "confirm":{"type":"boolean"} } }
          """,
          requires: Req(module: "workflow")),

        // ---- Forms archetype (module: forms) — see architecture-app-archetypes.md ----
        C("capability.form_designer", ComponentTier.Capability, "Form Designer",
          "Inline, reorderable editor for a form template's questions (add/edit/reorder/type/options).",
          "In a Forms-archetype app: to build/edit a survey's questions. Bound to the formTemplate entity; edits its formField children.",
          """
          { "type":"object", "additionalProperties":false, "required":["template"], "properties":{
            "template":{"type":"string","description":"Entity key of the role:'formTemplate' entity."} } }
          """,
          requires: Req(module: "forms")),

        C("capability.form_runner", ComponentTier.Capability, "Form Runner",
          "Renders a dynamic form FROM a template's questions and records a response with one answer per question.",
          "In a Forms-archetype app: the 'take the survey / fill the form' experience. Generic — the inputs come from the question rows, not the manifest.",
          """
          { "type":"object", "additionalProperties":false, "required":["template"], "properties":{
            "template":{"type":"string","description":"Entity key of the role:'formTemplate' entity."} } }
          """,
          requires: Req(module: "forms")),

        C("capability.form_results", ComponentTier.Capability, "Form Results",
          "Aggregates answers per question into the right chart (choice→bar/pie, scale→average+distribution, yes/no→split, text→list).",
          "In a Forms-archetype app: the results dashboard for a template's responses.",
          """
          { "type":"object", "additionalProperties":false, "required":["template"], "properties":{
            "template":{"type":"string","description":"Entity key of the role:'formTemplate' entity."} } }
          """,
          requires: Req(module: "forms")),
    ];

    // ---- authoring helpers ---------------------------------------------------------------------

    private static ComponentDef C(
        string id, ComponentTier tier, string label, string description, string whenToUse,
        string configSchemaJson, ComponentRequires? requires = null, string[]? slots = null,
        string[]? bindings = null) =>
        new(id, tier, label, description, whenToUse, requires, JsonNode.Parse(configSchemaJson)!, slots, bindings);

    private static ComponentRequires Req(DataShape[]? dataShape = null, string? module = null) =>
        new(dataShape, module);

    private static DataShape Shape(string description, string? role = null, string[]? fieldTypes = null) =>
        new(description, role, fieldTypes);

    // ---- emission ------------------------------------------------------------------------------

    private static JsonObject ToJson(ComponentDef c)
    {
        var o = new JsonObject
        {
            ["id"] = c.Id,
            ["tier"] = c.Tier.ToString().ToLowerInvariant(),
            ["label"] = c.Label,
            ["description"] = c.Description,
            ["whenToUse"] = c.WhenToUse,
            ["configSchema"] = c.ConfigSchema.DeepClone(),
        };
        // Block-tier entries carry their AUTHORING identity, derived from BlockKinds so the map
        // stays single-sourced: `kind` is the literal value the model writes in block.kind (its
        // family is the prefix — not emitted separately, it would just cost tokens twice). A block
        // kind with no authoring name (widgets) is deprecated — still legal in stored documents,
        // but never offered to the model.
        if (c.Tier == ComponentTier.Block && c.Id.StartsWith("block.", StringComparison.Ordinal))
        {
            var canonical = c.Id["block.".Length..];
            if (BlockKinds.ByCanonical.TryGetValue(canonical, out var names) && names.Count > 0)
                o["kind"] = names[0];
            else if (BlockKinds.NotAuthorable.Contains(canonical))
                o["deprecated"] = true;
        }
        if (c.Requires is { } r)
        {
            var rj = new JsonObject();
            if (r.DataShape is { Length: > 0 })
            {
                var shapes = new JsonArray();
                foreach (var d in r.DataShape)
                {
                    var dj = new JsonObject { ["description"] = d.Description };
                    if (d.Role is not null) dj["role"] = d.Role;
                    if (d.FieldTypes is { Length: > 0 }) dj["fieldTypes"] = StrArray(d.FieldTypes);
                    shapes.Add(dj);
                }
                rj["dataShape"] = shapes;
            }
            if (r.Module is not null) rj["module"] = r.Module;
            o["requires"] = rj;
        }
        if (c.Slots is { Length: > 0 }) o["slots"] = StrArray(c.Slots);
        if (c.Bindings is { Length: > 0 }) o["bindings"] = StrArray(c.Bindings);
        return o;
    }

    private static JsonArray StrArray(IEnumerable<string> items)
    {
        var a = new JsonArray();
        foreach (var s in items) a.Add(s);
        return a;
    }

    private static IReadOnlyList<string> LoadFieldTypes()
    {
        var schema = JsonNode.Parse(Schemas.AppDefinitionSchemaJson)!;
        var en = schema["$defs"]?["field"]?["properties"]?["type"]?["enum"] as JsonArray;
        return en?.Select(x => x!.GetValue<string>()).ToList() ?? [];
    }
}

/// <summary>The five component tiers. Field renderers are governed by the App Definition
/// <c>field.type</c> enum; the rest are catalog entries.</summary>
public enum ComponentTier { Field, View, Widget, Block, Capability }

/// <summary>A precondition on the target entity's data shape, checked deterministically by the gate.
/// Either a field <see cref="Role"/> (e.g. "status") or one of a set of field <see cref="FieldTypes"/>
/// must be present on the entity.</summary>
public sealed record DataShape(string Description, string? Role = null, string[]? FieldTypes = null);

/// <summary>What a component needs to be legal: entity <see cref="DataShape"/> preconditions and/or an
/// enabled capability <see cref="Module"/> (e.g. "ai", "connectors", "workflow").</summary>
public sealed record ComponentRequires(DataShape[]? DataShape = null, string? Module = null);

/// <summary>One catalog entry. <see cref="ConfigSchema"/> is the JSON Schema for this component's
/// <c>config</c> object; <see cref="Slots"/> (blocks only) lists the tiers that may nest inside;
/// <see cref="Bindings"/> lists the binding modes (collection | record | draft) the component is
/// legal under — the design gate enforces it, the design prompt teaches it.</summary>
public sealed record ComponentDef(
    string Id,
    ComponentTier Tier,
    string Label,
    string Description,
    string WhenToUse,
    ComponentRequires? Requires,
    JsonNode ConfigSchema,
    string[]? Slots = null,
    string[]? Bindings = null);

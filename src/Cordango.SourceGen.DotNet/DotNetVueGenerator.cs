// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.SourceGen.DotNet.Emit;
using Cordango.SourceGen.Common;

namespace Cordango.SourceGen.DotNet;

/// <summary>
/// The standalone target: one application, as conventional source you own.
///
/// <para>ASP.NET Core with MVC controllers, EF Core against PostgreSQL, ASP.NET Core Identity for
/// sign-in, Vue 3 and Vuetify on the front. Nothing about the generated result depends on Cordango
/// at runtime: no licence check, no account, no model API, no phone home. It keeps working if this
/// project stops existing.</para>
///
/// <para><b>Its capabilities are declared before its emitters exist, and that is deliberate.</b>
/// A target is two separable things — a claim about what it can build, and the code that builds it —
/// and the claim is what <c>cordango check --target</c> needs. Publishing it first means a person
/// can find out today whether their application is compatible, and it means the capability list is
/// written from the language rather than backwards from whatever the emitters happened to
/// implement.</para>
/// </summary>
public sealed class DotNetVueGenerator : IAppSourceGenerator, ICustomCodeScanner
{
    public string Id => "dotnet-vue";

    /// <summary>
    /// Reading custom C# is this target's job because writing C# is.
    ///
    /// <para>The interface hangs off the GENERATOR rather than being registered on its own, because
    /// <c>Targets.Registered</c> is a flat list of generators and a scanner nobody can reach is no
    /// use. <c>Targets.All.OfType&lt;ICustomCodeScanner&gt;()</c> is then the whole lookup, and the
    /// registry needs no new concept for a second language to join.</para>
    /// </summary>
    private readonly Custom.CSharpScanner _scanner = new();

    public string Language => _scanner.Language;

    public string SourceExtension => _scanner.SourceExtension;

    public CustomScanResult Scan(CustomCodeContext context) => _scanner.Scan(context);

    /// <summary>Zero major: the emitters are not written. The version is in the build metadata of
    /// everything this produces, so it starts honest rather than starting at 1.0.</summary>
    public string Version => BuildVersion.Current;

    public GeneratorCapabilities Capabilities { get; } = new(
        // Every screen the language has, except the two that are ABOUT other applications. A
        // standalone application can be large and can be complicated; what it cannot be is plural.
        Blocks: CapabilitySet.Of(
        [
            // Layout and text.
            "section", "row", "columns", "stack", "grid", "card", "tabs", "split", "text",
            // Records and fields.
            "fields", "field", "cell", "chip", "avatar", "form", "hub", "settings", "repeat",
            // Collections.
            "view", "table", "board", "calendar", "child",
            // Figures.
            "stat", "tiles", "chart", "progress", "widgets",
            // Behaviour.
            "process", "action", "create", "control", "filterbar", "period", "matrix",
            // Time and structure.
            "gantt", "timeline", "orgchart",
            // Forms.
            "intake", "answers",
            // Anything else a page can hold.
            "externalEmbed",
        ],
            withheld:
            [
                // EVERY refusal on this target is a platform capability, and every message says so
                // the same way: name the feature, say what the platform does with it, say what a
                // standalone application has instead, and give both ways forward. An earlier draft
                // argued that related-apps was different in kind because the question is
                // unanswerable with one application rather than merely unimplemented. True, and
                // useless to a reader: from their side both are "the platform does this and this
                // build does not", and cross-application data is the thing the platform is FOR.
                ("history",
                 "record history is a Cordango Platform feature. The platform keeps a field-level "
                 + "audit trail for every record automatically (what changed, who changed it, and "
                 + "the value before and after), and this block is the screen that shows it. A "
                 + "standalone application keeps technical logs but no business audit trail, so "
                 + "there is nothing behind the block to render. Remove the block to build "
                 + "standalone, or run this application on Cordango Platform, where the trail is "
                 + "kept for you with nothing to configure"),
                ("relatedApps",
                 "related apps is a Cordango Platform feature. On the platform every application "
                 + "shares one set of People and Organizations, so this block can show the deals, "
                 + "tickets and projects from across the company that point at this record. A "
                 + "standalone application has its own local People and Organizations and nothing "
                 + "installed beside it, so there is nothing for the block to look through. Remove "
                 + "the block to build standalone, or run this application on Cordango Platform"),
                ("documents",
                 "documents is a Cordango Platform feature. On the platform every workspace has one "
                 + "Documents app, and this block shows a record's own documentation space or the "
                 + "reader's notes and shared spaces, stored as pages, versioned on every save and "
                 + "governed by the platform's document service. A standalone application has no "
                 + "document store, so there is nothing for the block to show. Remove the block to "
                 + "build standalone, or run this application on Cordango Platform"),
            ]),

        Effects: CapabilitySet.Of(
            ["createRecord", "updateRecord", "createForEach", "deleteRecord", "notify", "email", "webhook"],
            withheld:
            [
                ("enrich",
                 "enrichment is a Cordango Platform feature. The platform researches a company "
                 + "from its own website and files what it finds against your Organizations with "
                 + "the evidence attached. A standalone application ships with no model and no "
                 + "research pipeline, and is not meant to call one. Remove the effect to build "
                 + "standalone, or run this application on Cordango Platform"),
            ]),

        // Every trigger a SINGLE application can have. A standalone build runs its own background
        // service for the scheduled ones rather than borrowing a platform scheduler.
        //
        // The two cross-app triggers are withheld rather than unbuilt: they are not missing work, they
        // are meaningless here. A standalone build is one application, and one application has nobody
        // to subscribe to — an app that announced into an empty room would be indistinguishable from
        // one whose subscription silently never fires, which is the failure this whole classification
        // exists to prevent.
        Triggers: CapabilitySet.Of(
            ["record.created", "record.updated", "record.deleted", "field.changed", "schedule"],
            fallback: null,
            ("command.emitted",
             "a subscription to another application's announcement, and a standalone build is one "
             + "application — install both on Cordango Platform, where the other one exists to announce"),
            ("process.state_entered",
             "a subscription to another application's record entering a state, and a standalone build "
             + "is one application — install both on Cordango Platform, where the other one exists")),

        // Every field type, including attachment: a generated application stores uploads on its own
        // disk rather than in platform media storage.
        FieldTypes: CapabilitySet.Of(
        [
            "text", "longtext", "email", "url", "phone", "select", "multiselect", "boolean",
            "integer", "decimal", "money", "date", "datetime", "reference", "attachment", "json",
        ]),

        // The platform primitives a standalone build carries a local equivalent of. Anything else
        // named by `targetApp` is a separately installed application, which cannot exist here.
        PlatformTargets: CapabilitySet.Of(
            ["platform", "core_organizations"],
            fallback:
            "references between applications are a Cordango Platform feature. The platform runs "
            + "several applications on one shared company graph, so a record in one can point at a "
            + "record in another. A standalone build is a single application: it carries its own "
            + "People, Organizations, Departments and Groups and can reference those, but there is "
            + "no second application for this field to resolve against. Point the field at a local "
            + "entity to build standalone, or run this application on Cordango Platform",
            withheld:
            [
                ("core_documents",
                 "documents are a Cordango Platform feature. On the platform a record may own a "
                 + "documentation space in the workspace's Documents app, and this reference names "
                 + "it. A standalone application has no document store for the reference to point "
                 + "into. Remove the field to build standalone, or run this application on Cordango "
                 + "Platform"),
            ]),

        PlatformEntities: CapabilitySet.Of(
            ["person", "department", "group", "organization", "contact"],
            fallback:
            "a standalone application carries local People, Organizations, Departments and Groups. "
            + "Platform entities beyond those have nothing local to map onto, so this reference "
            + "would have nowhere to point. Use one of the local entities to build standalone, or "
            + "run this application on Cordango Platform"),

        // An ordered series, where each row reads the one before it. The EXPRESSIONS are already
        // shared — `ComputedExpr` parses and evaluates with one implementation — so what this
        // commits to is the RECOMPUTE CASCADE: working out which records a write disturbs, in what
        // order, without recomputing the world. That is the hardest thing in the platform runtime
        // and it took two passes to make fast there. It is in scope because the alternative is a
        // standalone target that cannot build the Budget Planner, and a calculation plane that only
        // works on the hosted product is not the calculation plane being sold.
        SeriesAndPrev: true,

        // Rollups up a reference are a GROUP BY. Windowed and matched rollups are a declarative
        // SUMIFS over a range or across siblings, and they lean on the same cascade.
        WindowedRollups: true);

    /// <summary>
    /// The application, as files.
    ///
    /// <para>Three layers, in this order: the SCAFFOLD (everything an application has before it has
    /// any entities — host, sign-in, directory, packaging, the runtime as source), the BACKEND
    /// (one class, one EF configuration, one descriptor and one controller per entity, plus the
    /// compiled permission rules and the first migration), and the FRONT END (one page per screen,
    /// a typed client, the router). Later layers overwrite earlier ones by path, which is how the
    /// generated <c>AppDbContext</c> replaces the scaffold's empty one instead of colliding with
    /// it.</para>
    /// </summary>
    public GenerateResult Generate(GenerateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var workspace = WorkspaceModel.From(request.Workspace);
        var allowIncomplete = request.Options?["allowIncomplete"]?.GetValue<bool>() ?? false;
        var runtimeAsPackage = request.Options?["runtimeAsPackage"]?.GetValue<bool>() ?? true;

        // THE APPS OF A WORKSPACE BECOME ONE APPLICATION HERE.
        //
        // They are authoring units, not runtime boundaries: one database, one router, one namespace,
        // one sign-in. So they are LINKED — merged into a single manifest, with the handful of keys
        // that a merged deployment can only have once renamed where two apps chose the same one, and
        // every reference to a renamed key moved with it. From the next line down, every emitter
        // sees one application and needs to know nothing about any of this.
        //
        // The rename is the build's job rather than the author's on purpose. The same two apps have
        // to run on Cordango Platform, where each keeps its own tables behind its own handle and two
        // `dashboard`s are perfectly fine. Asking somebody to rename one for the benefit of one
        // target would make the definition target-specific, which is the one thing it exists not to
        // be. See WorkspaceLink.
        var previousNames = Names(request.Options?["names"]);
        var linked = WorkspaceLink.Merge(
            request.Workspace.Identity,
            [.. workspace.Apps.Select((a, i) => (a.Key, request.Workspace.Apps[i].Manifest))],
            previousNames);

        if (linked.Problems.Count > 0) return GenerateResult.Failed([.. linked.Problems]);

        var app = AppModel.From(new CompiledAppArtifact(
            linked.Manifest,
            linked.Manifest,
            request.Workspace.Apps[0].DefinitionHash,
            request.Workspace.Apps[0].Compiler));

        // Custom code is still per app — it is attributed C# in one app's folder — and the merged
        // application carries all of it.
        var customSources = request.CustomFor(workspace.Apps[0].Key);

        // The capability gate, and it does NOT stop the build.
        //
        // It used to. One `history` block on one screen refused an entire application — every entity,
        // every workflow, every screen — because of a card on one page. That ratio is indefensible:
        // the definition is valid, the ninety per cent this target CAN build is worth having, and the
        // person can decide whether the missing tenth matters to them.
        //
        // So a capability this target lacks is reported and the thing is left out: a block becomes a
        // card saying so, an effect does not fire, a reference to another application's record stays
        // an unresolved id. Nothing is silently dropped, and the build still refuses by default —
        // `--allow-incomplete` is how somebody says they know — but what they are refusing is now a
        // list they can read rather than a wall.
        //
        // Block kinds are dropped from this list because the web emitter reports them itself, with
        // the same codes and a path pointing at the page it actually rendered. Two entries for one
        // card, one of them saying "not yet" about something that will never come, is worse than
        // either alone.
        //
        // Asked of EVERY app, against its own definition rather than against the linked manifest. Two
        // reasons, and the second is the one that bites: a path like `$.entities[3].fields[1]` has to
        // point into a document somebody can open, and after the link there is no such document — the
        // entity may have been renamed and it sits at a different index beside another app's. Asking
        // only the first app, which is what this did while a build was one app, would have let the
        // second app's unsupported features through in silence.
        var unsupported = request.Workspace.Apps
            .SelectMany(a => TargetValidator.Validate(a.Definition, Capabilities, workspace.AppKeys))
            .Where(d => d.Code is not (DiagnosticCodes.HistoryBlock
                or DiagnosticCodes.RelatedAppsBlock
                or DiagnosticCodes.UnsupportedBlock))
            .ToList();

        // BEFORE anything is written. If the sources and the definition disagree, the application
        // this would produce is not the one the definition describes — and its recorded hash would
        // say otherwise. Nothing about a partial answer is useful here.
        if (Emit.CustomEmitter.Mismatch(app, customSources) is { } mismatch)
            return GenerateResult.Failed(mismatch);

        var files = new Dictionary<string, GeneratedFile>(StringComparer.Ordinal);
        var warnings = new List<Diagnostic>();

        void Add(GeneratedFile file) => files[file.RelativePath] = file;

        // The WORKSPACE names the deployment — the database, the container, the title on the sign-in
        // page — and the APP names the code. They have to be told apart, and for a moment they were
        // not: the scaffold took `workspace.Namespace` for all three while every emitter below wrote
        // `app.Namespace`, so a workspace called "Operations" holding `project_intake` produced a
        // Program.cs with `using Operations.Data;` and an AppDbContext in `ProjectIntake.Data`. The
        // build failed on the first file, and nothing in the message said the two halves had been
        // named by different things.
        //
        // One app per build is what makes `app.Namespace` the right answer here (CORD2310 refuses
        // more). When several apps in one deployment lands, the host project gets a namespace of its
        // own again and each app keeps its own — and this line is where that choice is made.
        var scaffold = new ScaffoldOptions(workspace.Name, workspace.Key, app.Namespace,
            RuntimeAsPackage: runtimeAsPackage, HasCustomCode: customSources is not null);
        foreach (var file in Scaffold.Files(scaffold)) Add(file);

        Add(BackendEmitter.DbContext(app));
        Add(BackendEmitter.Descriptors(app));
        Add(BackendEmitter.Setup(app));
        Add(BackendEmitter.Permissions(app));
        Add(BackendEmitter.Commands(app));
        if (FormsEmitter.Emit(app) is { } forms) Add(forms);
        if (Emit.CalendarEmitter.Emit(app) is { } calendar) Add(calendar);
        Add(SchemaEmitter.Emit(app));
        Add(WorkflowEmitter.Workflows(app));

        foreach (var file in Emit.CustomEmitter.Emit(customSources)) Add(file);
        foreach (var file in Emit.CustomEmitter.Adapters(app)) Add(file);

        foreach (var entity in app.Entities)
        {
            Add(EntityEmitter.Emit(app, entity));
            Add(ConfigurationEmitter.Emit(app, entity));
            Add(BackendEmitter.Controller(app, entity));
        }

        foreach (var file in MigrationEmitter.Emit(app)) Add(file);

        foreach (var entity in app.Entities)
        {
            if (BackendEmitter.AutoFields(app, entity) is { } hook) Add(hook);
            if (BackendEmitter.Computed(app, entity) is { } computed) Add(computed);
            if (BackendEmitter.ComputedHook(app, entity) is { } recompute) Add(recompute);

            // Figures counted from OTHER records, and the hook that keeps them right when those
            // records change. Emitted together because neither is any use without the other.
            if (BackendEmitter.Rollups(app, entity) is { } rollups) Add(rollups);
            if (BackendEmitter.Series(app, entity) is { } series) Add(series);
            if (BackendEmitter.RollupHook(app, entity) is { } cascade) Add(cascade);
        }

        // The chain itself: one file naming every recompute, in the order the definition's own
        // rollup graph requires.
        if (BackendEmitter.RollupCascade(app) is { } chain) Add(chain);
        if (BackendEmitter.SeedFinalizer(app) is { } finalizer) Add(finalizer);

        // A dataset to open the application on. Derived from the seed alone, so two builds of the
        // same definition produce the same rows.
        Add(Common.SeedEmitter.Emit(app, request.Options?["seed"]?.GetValue<int>() ?? 42));

        var web = WebEmitter.Emit(app, allowIncomplete, Capabilities, Id);
        foreach (var file in web.Files) Add(file);
        warnings.AddRange(web.Warnings);

        // Everything this build could not do: what the target will never do, what the emitters have
        // not got to yet, and the screens that fall into either. One list to decide whether the build
        // needs permission; two lists in the README, because "not supported" and "not yet" are
        // different news for whoever inherits the application.
        var incomplete = new List<Diagnostic>([.. unsupported, .. web.Unsupported, .. NotYetEmitted(app)]);

        if (!allowIncomplete && incomplete.Count > 0)
            return GenerateResult.Failed([.. incomplete]);

        warnings.AddRange(incomplete);

        // Re-run the README with the scar, now that it is known whether there is one.
        if (incomplete.Count > 0)
        {
            var scar = PartialBuildSection(incomplete);
            foreach (var file in Scaffold.Files(scaffold with { PartialBuildSection = scar }))
                if (file.RelativePath == "README.md")
                    Add(file);
        }

        return new GenerateResult(
            [.. files.Values.OrderBy(f => f.RelativePath, StringComparer.Ordinal)],
            warnings,
            [])
        {
            // Recorded in cordango.build.json and handed back on the next build, so a name this link
            // decided never moves under a deployment that is already running.
            Names = linked.Names,
        };
    }

    /// <summary>
    /// Names an earlier build of this workspace assigned, handed back so they do not move.
    ///
    /// <para>A rename is a migration — it changes a table and an address somebody bookmarked — so the
    /// link records what it decided in <c>cordango.build.json</c> and the CLI passes the previous
    /// answer back in. Absent, or unreadable, means "decide afresh", which is right for a first
    /// build and harmless for any other: the same collisions produce the same names.</para>
    /// </summary>
    private static IReadOnlyDictionary<string, string>? Names(JsonNode? node)
    {
        if (node is not JsonObject map) return null;

        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in map)
            if (AppModel.Str(value) is { Length: > 0 } assigned) names[key] = assigned;

        return names.Count > 0 ? names : null;
    }

    /// <summary>
    /// What the definition asks for that the emitters do not produce yet.
    ///
    /// <para><b>Reported, never skipped.</b> These are the gaps that would otherwise be invisible:
    /// an application whose screens all render and whose approvals silently send no notification
    /// looks finished, and the person testing it has no reason to suspect otherwise. A missing
    /// screen at least leaves a hole.</para>
    ///
    /// <para>The capability declaration says this target SUPPORTS all of these, and that stays true:
    /// a capability is a claim about the target, and these are the emitters catching up to it. When
    /// they do, the diagnostics disappear on their own.</para>
    /// </summary>
    private static IEnumerable<Diagnostic> NotYetEmitted(AppModel app)
    {
        // A total that is an input to itself has no answer to compute towards, and the chain that
        // kept them right would recurse until the stack ran out. Nothing in any application anybody
        // has written does this; it is checked because the emitted code would be the thing that
        // failed, at run time, in production.
        if (Emit.RollupGraph.IsCyclic(app))
            yield return new Diagnostic(NotYetCodes.RollupCycle,
                "the rollups in this application count each other in a circle, so no order works "
                + "them out. Break the loop — one of the totals has to be worked out from something "
                + "other than a figure that counts it.",
                "$.entities");

        // The calendar flag used to be refused outright here, and that was one claim too wide. It
        // says two things at once: "these records belong in the responsible person's calendar", which
        // one application can honour completely for its OWN records and now does — the personal
        // calendar at /api/me/calendar and the screen over it — and "alongside every other app's
        // dates", which is a workspace surface a single generated application genuinely has nothing
        // to span. Only the second is still missing, so only the second is reported.
        //
        // The flag reaching the emitter UNRESOLVED is a different failure and a louder one. The
        // compiler resolves `calendar: true` into a binding and refuses the build when it cannot, so
        // a bare flag here means something bypassed that — and emitting nothing would put the records
        // in nobody's calendar without saying so.
        foreach (var entity in app.Entities.Where(e => e.Json["calendar"] is not null))
        {
            if (app.Calendars.Any(e => e.Key == entity.Key)) continue;

            yield return new Diagnostic(NotYetCodes.Calendar,
                $"the entity '{entity.Key}' is marked to appear in people's calendars, but its flag "
                + "reached this generator unresolved — no start date and no person. Build through "
                + "`cordango build`, which resolves it, rather than handing the generator a raw "
                + "definition. Its records will not appear in anybody's calendar.",
                $"$.entities[?(@.key=='{entity.Key}')].calendar");
        }

        // The cross-application feed — one person, one calendar, every app in a workspace — is NOT
        // reported here, and that is a deliberate change. A CORD23xx stops a build until somebody
        // passes --allow-incomplete, which is the right treatment for "the emitters have not got to
        // this" and the wrong one for "a single application has no other applications to span". The
        // second is a property of the target, it will not change with a release, and a build that
        // failed on it for ever would be telling somebody to wait for something that is not coming.
        // The scope is stated where whoever inherits the application will read it: the header of the
        // generated AppCalendar.cs.

        for (var i = 0; i < app.Workflows.Count; i++)
        {
            var workflow = app.Workflows[i];
            var key = AppModel.Str(workflow["key"])
                ?? i.ToString(System.Globalization.CultureInfo.InvariantCulture);

            var trigger = workflow["trigger"] as JsonObject;
            var @event = AppModel.Str(trigger?["event"]);

            // A trigger this target does not wire. `schedule` needs a host that is awake and a timer
            // to wake it, which is a different piece of machinery from a hook on a write.
            if (@event is null || !Emit.WorkflowEmitter.Triggers.Contains(@event))
            {
                yield return new Diagnostic(NotYetCodes.Trigger,
                    $"the workflow '{key}' triggers on '{@event ?? "nothing"}', which is not wired yet, "
                    + "so nothing will run.",
                    $"$.workflows[{i}].trigger");
                continue;
            }

            // A subscription the emitter could not resolve: the announcement names something no
            // command in this build emits, or a state of an entity with no lifecycle to hold it.
            // Reported rather than skipped, because a workflow wired to a name nothing announces is
            // one that never runs — and it looks entirely finished from the outside.
            if (Emit.WorkflowEmitter.Unresolved(app, trigger) is { } unresolved)
            {
                yield return new Diagnostic(NotYetCodes.Trigger,
                    $"the workflow '{key}' subscribes to '{unresolved}', and nothing in this build "
                    + "announces it. Check the name against the command's `emits`, or the entity and "
                    + "state against the lifecycle that governs them — a subscription to a name "
                    + "nobody makes never runs.",
                    $"$.workflows[{i}].trigger");
                continue;
            }

            // A guard that cannot be written would make the workflow fire in cases the definition
            // excludes — the same hazard as a command's guard, and louder here because a workflow
            // runs without anybody watching.
            if (!Emit.ConditionEmitter.TryEmit(workflow["when"], out _))
                yield return new Diagnostic(NotYetCodes.Guard,
                    $"the workflow '{key}' has a condition this generator cannot write, so it would "
                    + "run in cases the definition excludes.",
                    $"$.workflows[{i}].when");

            var unwritten = AppModel.Arr(workflow["effects"]).OfType<JsonObject>()
                .Select(e => AppModel.Str(e["type"]))
                .Where(type => type is null || !Emit.WorkflowEmitter.Emitted.Contains(type))
                .Select(type => type ?? "an unnamed effect")
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (unwritten.Count > 0)
                yield return new Diagnostic(NotYetCodes.Effect,
                    $"the workflow '{key}' declares {string.Join(" and ", unwritten)} effect(s), which are "
                    + "not generated yet. Its trigger fires and its other effects run; these do not.",
                    $"$.workflows[{i}].effects");
        }

        foreach (var command in app.Commands)
        {
            // A command's effects are the workflow emitter's, run by the workflow runner — so what
            // it can write here is exactly what it can write there. `email` and `webhook` are the
            // ones left: both need something outbound, and an application that silently sent
            // nothing would look like one whose mail was being swallowed.
            var unsent = command.Effects
                .Where(e => AppModel.Str(e["type"]) is not "notify")
                .Where(e => Emit.WorkflowEmitter.Effect(app, command.Entity, e) is null)
                .Select(e => AppModel.Str(e["type"]) ?? "an unnamed effect")
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (unsent.Count > 0)
                yield return new Diagnostic(NotYetCodes.Effect,
                    $"'{command.Label}' declares {string.Join(" and ", unsent)} effect(s), which are not "
                    + "generated yet. The command runs and moves the record; those effects do not fire. "
                    + "Its other effects DO.",
                    $"$.commands[?(@.key=='{command.Key}')].effects");

            // A guard the emitter cannot write must not become an application without one. The
            // command would run, look correct, and be more permissive than the definition — the
            // only kind of generator bug that is invisible in the output.
            if (!Emit.ConditionEmitter.TryEmit(command.Json["when"], out _))
                yield return new Diagnostic(NotYetCodes.Guard,
                    $"'{command.Label}' has a guard this generator cannot write, so the command would "
                    + "be generated WITHOUT it and would run in cases the definition refuses.",
                    $"$.commands[?(@.key=='{command.Key}')].when");
        }

        foreach (var diagnostic in UnqueryableFilters(app)) yield return diagnostic;

        foreach (var entity in app.Entities)
            foreach (var field in entity.AuthoredFields.Where(f => f.Computed is not null))
            {
                // Anything the emitters wrote needs no diagnostic — an expression, or a rollup that
                // became a query. What is left is what neither could write.
                if (Emit.ComputedEmitter.Expression(app, entity, field) is not null) continue;
                if (Emit.RollupEmitter.Query(app, entity, field) is not null) continue;
                if (Emit.ComputedEmitter.ReadsPrevious(field)
                    && Emit.ComputedEmitter.Expression(app, entity, field, inSeries: true) is not null) continue;

                var why = field.Computed?["rollup"] is not null
                    ? "is a rollup this generator cannot write — a filter outside simple "
                        + "comparisons, or a shape whose parts do not resolve"
                    : "is computed from something outside its own record, which is not generated yet";

                yield return new Diagnostic(NotYetCodes.Computed,
                    $"'{entity.Label}.{field.Label}' {why}. The column exists and stays empty.",
                    $"$.entities[?(@.key=='{entity.Key}')].fields[?(@.key=='{field.Key}')].computed");
            }
    }

    /// <summary>
    /// Filter leaves the generated query layer cannot answer.
    ///
    /// <para>These reach the browser as part of a view or a block source and are only found out
    /// about when somebody opens the screen and the request comes back refused. That is the worst
    /// place to learn it: the build said nothing, the application compiled, and the page shows an
    /// error about an operator to a person who did not write one.</para>
    ///
    /// <para>Walked over the manifest rather than over the models, because the same filter shape
    /// appears in a saved view, a block's <c>source</c>, a stat's aggregate and a child list, and
    /// each of those is reached by a different path. Only arrays actually called <c>filters</c> are
    /// read, which is what keeps a process rule's <c>when</c> — evaluated by the condition
    /// evaluator, not by the query layer — out of this.</para>
    /// </summary>
    private static IEnumerable<Diagnostic> UnqueryableFilters(AppModel app)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (leaf, path) in Filters(app.Manifest, "$"))
        {
            var reason = AppModel.Str(leaf["path"]) is { } hop
                ? $"a filter on '{hop}', which reads a field one reference away"
                : AppModel.Str(leaf["operator"]) is "overlaps"
                    ? "the 'overlaps' filter, which compares a row's own range against a window"
                    : null;

            if (reason is null || !seen.Add(path)) continue;

            yield return new Diagnostic(NotYetCodes.BlockOption,
                $"the dotnet-vue generator does not emit {reason} yet. The screen that reads it "
                + "would ask for something the generated API refuses.",
                path);
        }
    }

    private static IEnumerable<(JsonObject Leaf, string Path)> Filters(JsonNode? node, string path)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (name, child) in o)
                {
                    // A `computed` subtree is a ROLLUP's filters, which never reach the query layer:
                    // they are emitted as a predicate inside a recompute hook. RollupEmitter reports
                    // what it cannot write as CORD2305, and walking in here would say the same gap a
                    // second time in the language of a screen that does not exist.
                    if (name == "computed") continue;

                    if (name == "filters" && child is JsonArray leaves)
                    {
                        for (var i = 0; i < leaves.Count; i++)
                            if (leaves[i] is JsonObject leaf)
                                yield return (leaf, $"{path}.filters[{i}]");
                        continue;
                    }

                    foreach (var found in Filters(child, $"{path}.{name}")) yield return found;
                }
                break;

            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                    foreach (var found in Filters(a[i], $"{path}[{i}]")) yield return found;
                break;
        }
    }

    /// <summary>
    /// The permanent mark a knowingly incomplete build leaves on the application it produced.
    ///
    /// <para>In the README as well as in <c>cordango.build.json</c>, because the metadata is read by
    /// tools and the README is read by people — and the person who inherits this repository in a
    /// year is the one who needs to know that some screens were never generated.</para>
    /// </summary>
    private static string PartialBuildSection(IReadOnlyList<Diagnostic> unsupported)
    {
        // CORD21xx is "this target cannot", CORD23xx is "these emitters do not yet". Separated
        // because they are different news: one is a decision somebody should make about which
        // product they want, and the other goes away on its own in a later release. Merged into one
        // list, a reader treats the whole thing as a to-do and waits for releases that will not come.
        var never = unsupported.Where(d => d.Code.StartsWith("CORD21", StringComparison.Ordinal)).ToList();
        var notYet = unsupported.Except(never).ToList();

        var lines = new List<string>
        {
            "",
            "## Partial build",
            "",
            "**This application was generated with `--allow-incomplete`, and parts of it are missing.**",
            "Nothing was dropped silently: every gap is listed below, and the same list is in",
            "`cordango.build.json`, so a partial build can never pass for a complete one later.",
            "",
            "Where a SCREEN asked for something that could not be drawn, the page shows a card saying so",
            "in its place. Where BEHAVIOUR is missing — an effect, a workflow, a computed field — there is",
            "nothing to see at all, which is why it is written down here.",
            "",
            "The data model, the API, the permissions and the commands are complete.",
            "",
        };

        if (never.Count > 0)
        {
            lines.Add("### Not supported by this target");
            lines.Add("");
            lines.Add("These need Cordango Platform — record history needs a field-level audit trail a");
            lines.Add("standalone application does not keep, and cross-application references need the other");
            lines.Add("applications to be there. They will not appear in a later release of this generator.");
            lines.Add("Remove them from the definition, or run the application on the platform.");
            lines.Add("");

            foreach (var diagnostic in Ordered(never)) lines.Add(Entry(diagnostic));
            lines.Add("");
        }

        if (notYet.Count > 0)
        {
            lines.Add("### Not generated yet");
            lines.Add("");
            lines.Add("The definition is fine and this target intends to support all of it. These are the");
            lines.Add("emitters catching up, and a later release will produce them with no change to your");
            lines.Add("definition.");
            lines.Add("");

            foreach (var diagnostic in Ordered(notYet)) lines.Add(Entry(diagnostic));
            lines.Add("");
        }

        lines.Add("Rebuilding without `--allow-incomplete` will refuse until these are resolved.");
        lines.Add("");
        return string.Join('\n', lines);

        static IEnumerable<Diagnostic> Ordered(IEnumerable<Diagnostic> diagnostics) =>
            diagnostics
                .OrderBy(d => d.JsonPath ?? "", StringComparer.Ordinal)
                .ThenBy(d => d.Code, StringComparer.Ordinal);

        static string Entry(Diagnostic diagnostic) =>
            $"- `{diagnostic.Code}` at `{diagnostic.JsonPath}` — {diagnostic.Message}";
    }
}

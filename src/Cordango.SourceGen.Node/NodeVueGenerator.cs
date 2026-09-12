// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.SourceGen.Common;
using Cordango.SourceGen.Node.Emit;

namespace Cordango.SourceGen.Node;

/// <summary>
/// The Node target: one application, as conventional TypeScript you own.
///
/// <para>Node and Express on the back, PostgreSQL underneath — a server when there is one and
/// PGlite in-process when there is not — and the SAME Vue 3 and Vuetify front end
/// <c>dotnet-vue</c> emits. Nothing about the generated result depends on Cordango at runtime: no
/// licence check, no account, no model API, no phone home.</para>
///
/// <para><b>The web half is not emitted here, and that is the point of the target.</b>
/// <c>WebEmitter</c> in <c>Cordango.SourceGen.Common</c> is a REST client that never asks what the
/// backend is written in, so this generator calls it rather than owning a copy. What is left for a
/// target to do is answer the same HTTP contract — which means a screen that renders on one stack
/// and not the other would be a bug in one shared file rather than a divergence between two
/// codebases that will never be diffed.</para>
/// </summary>
public sealed class NodeVueGenerator : IAppSourceGenerator
{
    public string Id => "node-vue";

    public string Version => BuildVersion.Current;

    /// <summary>
    /// What this target can and cannot express.
    ///
    /// <para>Identical to <c>dotnet-vue</c>'s, and identical for a reason rather than by copying: a
    /// capability is a claim about what a STANDALONE application can be, and both targets produce
    /// one. Every refusal here is a Cordango Platform feature — record history needs an audit trail
    /// a standalone application does not keep, cross-application references need the other
    /// applications to be installed — and none of them is a fact about Node.</para>
    ///
    /// <para>What differs between the two targets is what the EMITTERS have got to, which is
    /// reported as <c>CORD23xx</c> and disappears on its own in a later release. Declaring a
    /// narrower capability set for the newer target would confuse "this will never work" with "this
    /// is not written yet", which is the distinction the whole classification exists to keep.</para>
    /// </summary>
    public GeneratorCapabilities Capabilities { get; } = new(
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
            "process", "action", "create", "control", "filterbar",
            // Time and structure.
            "gantt", "timeline", "orgchart",
            // Forms.
            "intake", "answers",
            // Anything else a page can hold.
            "externalEmbed",
        ],
            withheld:
            [
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

        Triggers: CapabilitySet.Of(
            ["record.created", "record.updated", "record.deleted", "field.changed", "schedule"],
            fallback: null,
            ("command.emitted",
             "a subscription to another application's announcement, and a standalone build is one "
             + "application — install both on Cordango Platform, where the other one exists to announce"),
            ("process.state_entered",
             "a subscription to another application's record entering a state, and a standalone build "
             + "is one application — install both on Cordango Platform, where the other one exists")),

        FieldTypes: CapabilitySet.Of(
        [
            "text", "longtext", "email", "url", "phone", "select", "multiselect", "boolean",
            "integer", "decimal", "money", "date", "datetime", "reference", "attachment", "json",
        ]),

        PlatformTargets: CapabilitySet.Of(
            ["platform", "core_organizations"],
            fallback:
            "references between applications are a Cordango Platform feature. The platform runs "
            + "several applications on one shared company graph, so a record in one can point at a "
            + "record in another. A standalone build is a single application: it carries its own "
            + "People, Organizations, Departments and Groups and can reference those, but there is "
            + "no second application for this field to resolve against. Point the field at a local "
            + "entity to build standalone, or run this application on Cordango Platform"),

        PlatformEntities: CapabilitySet.Of(
            ["person", "department", "group", "organization", "contact"],
            fallback:
            "a standalone application carries local People, Organizations, Departments and Groups. "
            + "Platform entities beyond those have nothing local to map onto, so this reference "
            + "would have nowhere to point. Use one of the local entities to build standalone, or "
            + "run this application on Cordango Platform"),

        // The claim, not the emitters. Both are reported as CORD23xx until the recompute cascade is
        // written here — see NotYetEmitted.
        SeriesAndPrev: true,
        WindowedRollups: true);

    /// <summary>
    /// The application, as files.
    ///
    /// <para>Three layers, in this order: the SCAFFOLD (everything an application has before it has
    /// any entities — the host, the packaging, the Vue shell), the BACKEND (the entities, the
    /// roles, the commands, the computed figures and the routes), and the FRONT END (one page per
    /// screen, from the shared emitter). Later layers overwrite earlier ones by path.</para>
    /// </summary>
    public GenerateResult Generate(GenerateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var app = AppModel.From(request.App);
        var allowIncomplete = request.Options?["allowIncomplete"]?.GetValue<bool>() ?? false;

        // The capability gate, and it does NOT stop the build.
        //
        // A capability this target lacks is reported and the thing is left out: a block becomes a
        // card saying so, an effect does not fire. Nothing is silently dropped, and the build still
        // refuses by default — `--allow-incomplete` is how somebody says they know — but what they
        // are refusing is a list they can read rather than a wall.
        //
        // Block kinds are dropped from this list because the web emitter reports them itself, with
        // the same codes and a path pointing at the page it actually rendered.
        var unsupported = TargetValidator.Validate(request.App.Definition, Capabilities)
            .Where(d => d.Code is not (DiagnosticCodes.HistoryBlock
                or DiagnosticCodes.RelatedAppsBlock
                or DiagnosticCodes.UnsupportedBlock))
            .ToList();

        var files = new Dictionary<string, GeneratedFile>(StringComparer.Ordinal);
        var warnings = new List<Diagnostic>();

        void Add(GeneratedFile file) => files[file.RelativePath] = file;

        var scaffold = new ScaffoldOptions(app.Name, app.Key);
        foreach (var file in Scaffold.Files(scaffold)) Add(file);

        Add(EntityEmitter.Emit(app));
        Add(PermissionsEmitter.Emit(app));
        Add(CommandsEmitter.Emit(app));
        Add(SetupEmitter.App(app));
        Add(SetupEmitter.Routes(app));

        foreach (var entity in app.Entities)
        {
            if (AutoFieldsEmitter.Emit(entity) is { } auto) Add(auto);
            if (ComputedEmitter.Emit(app, entity) is { } computed) Add(computed);
        }

        // A dataset to open the application on. Derived from the seed alone, so two builds of the
        // same definition produce the same rows — and produced by the SHARED emitter, because what
        // an application should start with is a fact about the definition rather than about the
        // stack that serves it.
        Add(Common.SeedEmitter.Emit(
            app,
            request.Options?["seed"]?.GetValue<int>() ?? 42,
            "api/src/seed/seed.json"));

        var web = WebEmitter.Emit(app, allowIncomplete, Capabilities, Id);
        foreach (var file in web.Files) Add(file);
        warnings.AddRange(web.Warnings);

        var incomplete = new List<Diagnostic>([.. unsupported, .. web.Unsupported, .. NotYetEmitted(app)]);

        if (!allowIncomplete && incomplete.Count > 0)
            return GenerateResult.Failed([.. incomplete]);

        warnings.AddRange(incomplete);

        // Re-run the README with the scar, now that it is known whether there is one.
        if (incomplete.Count > 0)
        {
            var scar = PartialBuild.Section(incomplete);
            foreach (var file in Scaffold.Files(scaffold with { PartialBuildSection = scar }))
                if (file.RelativePath == "README.md")
                    Add(file);
        }

        return new GenerateResult(
            [.. files.Values.OrderBy(f => f.RelativePath, StringComparer.Ordinal)],
            warnings,
            []);
    }

    /// <summary>
    /// What the definition asks for that the emitters do not produce yet.
    ///
    /// <para><b>Reported, never skipped.</b> These are the gaps that would otherwise be invisible:
    /// an application whose screens all render and whose approvals silently send no notification
    /// looks finished, and the person testing it has no reason to suspect otherwise. A missing
    /// screen at least leaves a hole.</para>
    ///
    /// <para>The capability declaration says this target SUPPORTS all of these, and that stays
    /// true: a capability is a claim about the target, and these are the emitters catching up to
    /// it. When they do, the diagnostics disappear on their own.</para>
    /// </summary>
    private static IEnumerable<Diagnostic> NotYetEmitted(AppModel app)
    {
        // Workflows. Nothing in this target runs one yet: a trigger needs a hook on every write and
        // a schedule needs a host that is awake with a timer to wake it. An application whose
        // approvals silently never fire is the failure this diagnostic exists to prevent.
        for (var i = 0; i < app.Workflows.Count; i++)
        {
            var key = AppModel.Str(app.Workflows[i]["key"])
                ?? i.ToString(System.Globalization.CultureInfo.InvariantCulture);

            yield return new Diagnostic(NotYetCodes.Trigger,
                $"the workflow '{key}' is not generated by the node-vue target yet, so nothing will "
                + "run when its trigger fires. Its records are still created, read and changed "
                + "normally.",
                $"$.workflows[{i}].trigger");
        }

        foreach (var command in app.Commands)
        {
            // A command's state move, its own field writes, its guard and its notifications are all
            // generated. What is left is everything OUTBOUND or cross-record, which needs the
            // effect runner this target does not build yet.
            var unsent = command.Effects
                .Select(e => AppModel.Str(e["type"]))
                .Where(type => type is null || !CommandsEmitter.EmittedEffects.Contains(type))
                .Select(type => type ?? "an unnamed effect")
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (unsent.Count > 0)
                yield return new Diagnostic(NotYetCodes.Effect,
                    $"'{command.Label}' declares {string.Join(" and ", unsent)} effect(s), which the "
                    + "node-vue target does not generate yet. The command runs, moves the record and "
                    + "sends its notifications; these do not fire.",
                    $"$.commands[?(@.key=='{command.Key}')].effects");
        }

        // The calendar flag asks for something this target does not have and cannot fake: a surface
        // that spans EVERY app a person can reach, in one workspace, with one feed.
        foreach (var entity in app.Entities.Where(e => e.Json["calendar"] is not null))
            yield return new Diagnostic(NotYetCodes.Calendar,
                $"the entity '{entity.Key}' is marked to appear in people's calendars, which is a "
                + "cross-application surface the Cordango Platform provides and a standalone "
                + "application has nothing to span. Its records still appear in this application's "
                + "own calendar views.",
                $"$.entities[?(@.key=='{entity.Key}')].calendar");

        foreach (var entity in app.Entities)
        {
            if (ComputedEmitter.IsCyclic(entity))
                yield return new Diagnostic(NotYetCodes.RollupCycle,
                    $"the computed fields on '{entity.Label}' read each other in a circle, so no "
                    + "order works them out. Break the loop — one of them has to be worked out from "
                    + "something other than a figure that reads it.",
                    $"$.entities[?(@.key=='{entity.Key}')].fields");

            foreach (var field in entity.AuthoredFields)
            {
                if (ComputedEmitter.WhyNotEmitted(field) is not { } why) continue;

                yield return new Diagnostic(NotYetCodes.Computed,
                    $"'{entity.Label}.{field.Label}' {why}. The column exists and stays empty.",
                    $"$.entities[?(@.key=='{entity.Key}')].fields[?(@.key=='{field.Key}')].computed");
            }
        }

        // Published forms. The archetype needs a public token, a submission endpoint and a
        // proof-of-work challenge, none of which this target serves yet.
        if (app.Forms is not null)
            yield return new Diagnostic(NotYetCodes.BlockOption,
                "this application has the forms archetype, and the node-vue target does not serve "
                + "the public form endpoints yet. The entities behind it are generated and reachable "
                + "to anybody signed in; nothing anonymous can submit to them.",
                "$.entities");
    }
}

/// <summary>
/// The permanent mark a knowingly incomplete build leaves on the application it produced.
///
/// <para>In the README as well as in <c>cordango.build.json</c>, because the metadata is read by
/// tools and the README is read by people — and the person who inherits this repository in a year
/// is the one who needs to know that some screens were never generated.</para>
/// </summary>
internal static class PartialBuild
{
    public static string Section(IReadOnlyList<Diagnostic> unsupported)
    {
        // CORD21xx is "this target cannot", CORD23xx is "these emitters do not yet". Separated
        // because they are different news: one is a decision somebody should make about which
        // product they want, and the other goes away on its own in a later release.
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

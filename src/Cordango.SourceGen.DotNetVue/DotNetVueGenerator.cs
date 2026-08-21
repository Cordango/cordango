// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.SourceGen.DotNetVue.Emit;
using Cordango.SourceGen.DotNetVue.Model;

namespace Cordango.SourceGen.DotNetVue;

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
public sealed class DotNetVueGenerator : IAppSourceGenerator
{
    public string Id => "dotnet-vue";

    /// <summary>Zero major: the emitters are not written. The version is in the build metadata of
    /// everything this produces, so it starts honest rather than starting at 1.0.</summary>
    public string Version => "0.1.0-alpha";

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
            ]),

        Effects: CapabilitySet.Of(
            ["createRecord", "updateRecord", "createForEach", "notify", "email", "webhook"],
            withheld:
            [
                ("enrich",
                 "enrichment is a Cordango Platform feature. The platform researches a company "
                 + "from its own website and files what it finds against your Organizations with "
                 + "the evidence attached. A standalone application ships with no model and no "
                 + "research pipeline, and is not meant to call one. Remove the effect to build "
                 + "standalone, or run this application on Cordango Platform"),
            ]),

        // Every trigger the language has. A standalone application runs its own background service
        // for the scheduled ones rather than borrowing a platform scheduler.
        Triggers: CapabilitySet.Of(
            ["record.created", "record.updated", "record.deleted", "field.changed", "schedule"]),

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
            + "entity to build standalone, or run this application on Cordango Platform"),

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

        var app = AppModel.From(request.App);
        var allowIncomplete = request.Options?["allowIncomplete"]?.GetValue<bool>() ?? false;
        var runtimeAsPackage = request.Options?["runtimeAsPackage"]?.GetValue<bool>() ?? false;

        // The capability gate first, and separately from emission: an application this target cannot
        // build is refused with the reason, not attempted and abandoned half way.
        var refusals = TargetValidator.Validate(request.App.Definition, Capabilities);
        if (refusals.Count > 0) return GenerateResult.Failed([.. refusals]);

        var files = new Dictionary<string, GeneratedFile>(StringComparer.Ordinal);
        var warnings = new List<Diagnostic>();

        void Add(GeneratedFile file) => files[file.RelativePath] = file;

        var scaffold = new ScaffoldOptions(app.Name, app.Key, app.Namespace, RuntimeAsPackage: runtimeAsPackage);
        foreach (var file in Scaffold.Files(scaffold)) Add(file);

        Add(BackendEmitter.DbContext(app));
        Add(BackendEmitter.Descriptors(app));
        Add(BackendEmitter.Setup(app));
        Add(BackendEmitter.Permissions(app));
        Add(BackendEmitter.Commands(app));

        foreach (var entity in app.Entities)
        {
            Add(EntityEmitter.Emit(app, entity));
            Add(ConfigurationEmitter.Emit(app, entity));
            Add(BackendEmitter.Controller(app, entity));
        }

        foreach (var file in MigrationEmitter.Emit(app)) Add(file);

        foreach (var entity in app.Entities)
            if (BackendEmitter.AutoFields(app, entity) is { } hook)
                Add(hook);

        // A dataset to open the application on. Derived from the seed alone, so two builds of the
        // same definition produce the same rows.
        Add(SeedEmitter.Emit(app, request.Options?["seed"]?.GetValue<int>() ?? 42));

        var web = WebEmitter.Emit(app, allowIncomplete);
        foreach (var file in web.Files) Add(file);
        warnings.AddRange(web.Warnings);

        // Everything this build could not do, in one list: screens the emitters do not render yet
        // AND behaviour they do not execute yet. One list because the difference does not matter to
        // whoever inherits the application — what matters is that the definition asked for something
        // and the build did not deliver it.
        var incomplete = new List<Diagnostic>([.. web.Unsupported, .. NotYetEmitted(app)]);

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
    /// <para>The capability declaration says this target SUPPORTS all of these, and that stays true:
    /// a capability is a claim about the target, and these are the emitters catching up to it. When
    /// they do, the diagnostics disappear on their own.</para>
    /// </summary>
    private static IEnumerable<Diagnostic> NotYetEmitted(AppModel app)
    {
        for (var i = 0; i < app.Workflows.Count; i++)
        {
            var key = app.Workflows[i]["key"]?.GetValue<string>() ?? i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            yield return new Diagnostic("CORD2302",
                $"the workflow '{key}' is not generated yet, so nothing will run when its trigger fires.",
                $"$.workflows[{i}]");
        }

        foreach (var command in app.Commands)
        {
            // `notify` IS generated — it writes an in-app notification, which needs no mail server
            // and no outbound network. The rest do, and an application that silently sent nothing
            // would look like one whose emails were being swallowed.
            var unsent = command.Effects
                .Select(e => Model.AppModel.Str(e["type"]))
                .Where(type => type is not null and not "notify")
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (unsent.Count > 0)
                yield return new Diagnostic("CORD2303",
                    $"'{command.Label}' declares {string.Join(" and ", unsent)} effect(s), which are not "
                    + "generated yet. The command runs and moves the record; those effects do not fire. "
                    + "Its notify effects DO.",
                    $"$.commands[?(@.key=='{command.Key}')].effects");

            if (command.Json["when"] is not null)
                yield return new Diagnostic("CORD2304",
                    $"'{command.Label}' has a guard, which is not enforced yet. Its permission and its "
                    + "transition ARE enforced; the extra condition is not.",
                    $"$.commands[?(@.key=='{command.Key}')].when");
        }

        foreach (var entity in app.Entities)
            foreach (var field in entity.AuthoredFields.Where(f => f.Computed is not null))
                yield return new Diagnostic("CORD2305",
                    $"'{entity.Label}.{field.Label}' is computed, and the calculation is not generated yet. "
                    + "The column exists and stays empty.",
                    $"$.entities[?(@.key=='{entity.Key}')].fields[?(@.key=='{field.Key}')].computed");
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
        var lines = new List<string>
        {
            "",
            "## Partial build",
            "",
            "**This application was generated with `--allow-incomplete`, and parts of it are missing.**",
            "Where a screen used something the generator cannot render yet, the page shows a card saying so",
            "instead of the content. Where BEHAVIOUR is missing — an effect, a workflow, a computed field —",
            "there is nothing to see at all, which is why every gap is listed here by name.",
            "",
            "The data model, the API, the permissions and the commands are complete.",
            "",
        };

        foreach (var diagnostic in unsupported.OrderBy(d => d.JsonPath ?? "", StringComparer.Ordinal))
            lines.Add($"- `{diagnostic.Code}` at `{diagnostic.JsonPath}` — {diagnostic.Message}");

        lines.Add("");
        lines.Add("Rebuilding without `--allow-incomplete` will refuse until these are resolved.");
        lines.Add("");
        return string.Join('\n', lines);
    }
}

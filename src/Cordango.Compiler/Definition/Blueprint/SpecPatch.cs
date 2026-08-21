// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cordango.Definition.Blueprints;

/// <summary>
/// One change to a blueprint, expressed as an operation against an <b>id</b>.
///
/// <para>This is the shape every interview role returns. A role never emits a document: it emits
/// patches, deterministic code applies them, and <see cref="BlueprintGate"/> re-runs. That is what
/// keeps a proposer from quietly rewriting a decision the user already made — it can only say "add
/// this concept" or "change that field", never "here is the blueprint now".</para>
///
/// <para><b>Ids, not paths.</b> A JSON-pointer patch (<c>/concepts/3/fields/1</c>) breaks the moment
/// anything is inserted or reordered, and two proposals racing on the same list corrupt each other
/// silently. Targeting the immutable id means a patch says what it means regardless of ordering, and
/// a stale one is detectable rather than merely wrong.</para>
/// </summary>
/// <param name="Op">What to do — see <see cref="PatchOps"/>.</param>
/// <param name="TargetId">The blueprint id being changed. Null for whole-layer operations.</param>
/// <param name="Value">The spec object, shaped by the layer <see cref="Op"/> names.</param>
/// <param name="Reason">Why, in the user's terms. Shown beside the change when they review it.</param>
/// <param name="Provenance">Where the change came from. See <see cref="FieldProvenance"/>.</param>
/// <param name="BaseRevision">The revision this was computed against. A stale one is rejected.</param>
public sealed record SpecPatch(
    [property: JsonPropertyName("op")] string Op,
    [property: JsonPropertyName("targetId")] string? TargetId = null,
    [property: JsonPropertyName("value")] JsonElement? Value = null,
    [property: JsonPropertyName("reason")] string? Reason = null,
    [property: JsonPropertyName("provenance")] string? Provenance = null,
    [property: JsonPropertyName("baseRevision")] int? BaseRevision = null);

/// <summary>
/// The operation set. Deliberately small: one verb per layer rather than a verb per nested field,
/// because the layers match what a role is allowed to touch. The workflow proposer authors one
/// concept's whole lifecycle, so <c>update_workflow</c> carries the states and transitions together
/// — there is no <c>add_state</c>, and so no way to add a state to a workflow nobody reviewed.
/// </summary>
public static class PatchOps
{
    public const string SetSpark = "set_spark";
    public const string SetApp = "set_app";
    public const string SetIntent = "set_intent";
    public const string SetExperience = "set_experience";
    public const string AddPageStub = "add_page_stub";
    public const string UpdatePageStub = "update_page_stub";
    public const string UpsertSurface = "upsert_surface";
    public const string ComposePage = "compose_page";
    public const string SetLanding = "set_landing";

    public const string AddConcept = "add_concept";
    public const string UpdateConcept = "update_concept";
    public const string RemoveConcept = "remove_concept";

    public const string AddExternal = "add_external";
    public const string RemoveExternal = "remove_external";

    public const string AddRelationship = "add_relationship";
    public const string UpdateRelationship = "update_relationship";
    public const string RemoveRelationship = "remove_relationship";

    public const string AddReference = "add_reference";
    public const string RemoveReference = "remove_reference";

    public const string AddField = "add_field";
    public const string UpdateField = "update_field";
    public const string RemoveField = "remove_field";

    public const string AddWorkflow = "add_workflow";
    public const string UpdateWorkflow = "update_workflow";
    public const string RemoveWorkflow = "remove_workflow";

    public const string AddActor = "add_actor";
    public const string UpdateActor = "update_actor";
    public const string RemoveActor = "remove_actor";

    public const string AddJob = "add_job";
    public const string UpdateJob = "update_job";
    public const string RemoveJob = "remove_job";

    public const string AddScenario = "add_scenario";
    public const string UpdateScenario = "update_scenario";
    public const string RemoveScenario = "remove_scenario";

    public const string AddRequirement = "add_requirement";
    public const string UpdateRequirement = "update_requirement";
    public const string RemoveRequirement = "remove_requirement";

    public const string AddUncertainty = "add_uncertainty";
    public const string ResolveUncertainty = "resolve_uncertainty";

    public const string AddAssumption = "add_assumption";
    public const string AddUnsupported = "add_unsupported";

    public static readonly string[] All =
    [
        SetSpark, SetApp, SetIntent, SetExperience,
        AddPageStub, UpdatePageStub, UpsertSurface, ComposePage, SetLanding,
        AddConcept, UpdateConcept, RemoveConcept,
        AddExternal, RemoveExternal,
        AddRelationship, UpdateRelationship, RemoveRelationship,
        AddReference, RemoveReference,
        AddField, UpdateField, RemoveField,
        AddWorkflow, UpdateWorkflow, RemoveWorkflow,
        AddActor, UpdateActor, RemoveActor,
        AddJob, UpdateJob, RemoveJob,
        AddScenario, UpdateScenario, RemoveScenario,
        AddRequirement, UpdateRequirement, RemoveRequirement,
        AddUncertainty, ResolveUncertainty,
        AddAssumption, AddUnsupported,
    ];

    /// <summary>
    /// The spec layer an operation belongs to — used to check a role stayed inside its own layer,
    /// and to route a user's correction back to the right proposer.
    ///
    /// <para>Null for the BOOKKEEPING operations: requirements, uncertainties, assumptions and
    /// unsupported items are records ABOUT the blueprint rather than parts of it. An uncertainty in
    /// particular names the layer it affects on itself, so pinning one to a layer here would be
    /// inventing an answer. A silent default would have quietly filed every question under
    /// "concepts" and routed corrections to the wrong proposer.</para>
    /// </summary>
    public static string? LayerOf(string op) => op switch
    {
        SetSpark or SetApp or SetIntent => SpecLayers.Intent,
        AddActor or UpdateActor or RemoveActor => SpecLayers.Actors,
        AddConcept or UpdateConcept or RemoveConcept
            or AddExternal or RemoveExternal => SpecLayers.Concepts,
        AddRelationship or UpdateRelationship or RemoveRelationship
            or AddReference or RemoveReference => SpecLayers.Relationships,
        AddField or UpdateField or RemoveField => SpecLayers.Data,
        AddWorkflow or UpdateWorkflow or RemoveWorkflow => SpecLayers.Workflows,
        AddJob or UpdateJob or RemoveJob => SpecLayers.Jobs,
        SetExperience or AddPageStub or UpdatePageStub or UpsertSurface or ComposePage or SetLanding
            => SpecLayers.Experience,
        AddScenario or UpdateScenario or RemoveScenario => SpecLayers.Scenarios,
        _ => null,
    };
}

/// <summary>The outcome of applying a batch. <paramref name="Blueprint"/> is unchanged when
/// <paramref name="Errors"/> is non-empty — a batch applies wholly or not at all.</summary>
public sealed record PatchResult(Blueprint Blueprint, IReadOnlyList<string> Errors)
{
    public bool Ok => Errors.Count == 0;
}

/// <summary>
/// Applies patches deterministically.
///
/// <para><b>All or nothing.</b> A half-applied batch would leave a blueprint in a state nobody
/// authored and no gate ever saw. So the batch is applied to a working copy, validated, and only
/// then does it become the new revision.</para>
/// </summary>
public static class SpecPatches
{
    /// <summary>Applies a batch, producing the next revision. The revision number advances ONCE per
    /// batch, not once per patch: a proposal is one thing the user reviews.</summary>
    public static PatchResult Apply(Blueprint blueprint, IReadOnlyList<SpecPatch> patches,
        bool validate = true)
    {
        var errors = new List<string>();

        // Staleness first: a patch computed against an older revision may be undoing a decision made
        // since, and merging it would silently pick a winner nobody chose.
        foreach (var stale in patches.Where(p => p.BaseRevision is { } b && b != blueprint.Revision))
            errors.Add($"PATCH: '{stale.Op}' was computed against revision {stale.BaseRevision}, "
                + $"but the blueprint is now at {blueprint.Revision} — it has to be recomputed");
        if (errors.Count > 0) return new PatchResult(blueprint, errors);

        var working = blueprint;
        foreach (var patch in patches)
        {
            var (next, error) = ApplyOne(working, patch);
            if (error is not null) { errors.Add(error); return new PatchResult(blueprint, errors); }
            working = next;
        }

        working = working with
        {
            Revision = blueprint.Revision + 1,
            ParentRevision = blueprint.Revision,
            ContentHash = null,   // an unapproved revision has no frozen hash
        };

        if (validate)
        {
            // The DRAFT gate, not the approval gate. A blueprint under construction is unfinished by
            // definition — the concept pass runs four calls before the data pass, so every concept
            // legitimately has no fields when it lands. Holding a proposal to the approval gate
            // rejects it for gaps the interview's own sequencing created, and reports them against
            // layers the proposal never touched. What a draft must satisfy is that it does not
            // CONTRADICT itself; completeness is checked once, at approval.
            var gate = BlueprintGate.DraftErrors(working);
            if (gate.Count > 0)
            {
                // The batch was individually well-formed but leaves the blueprint inconsistent —
                // report against the ORIGINAL so the caller can re-propose rather than inherit it.
                errors.AddRange(gate);
                return new PatchResult(blueprint, errors);
            }
        }

        return new PatchResult(working, errors);
    }

    private static (Blueprint Next, string? Error) ApplyOne(Blueprint bp, SpecPatch p)
    {
        try
        {
            return p.Op switch
            {
                PatchOps.SetSpark => (bp with { Spark = Str(p) }, null),
                PatchOps.SetApp => (bp with { App = Val<AppSpec>(p) }, null),
                PatchOps.SetIntent => (bp with { Intent = Val<IntentSpec>(p) }, null),
                PatchOps.SetExperience => (bp with { Experience = Val<ExperienceSpec>(p) }, null),
                PatchOps.AddPageStub => AddPageStub(bp, p),
                PatchOps.UpdatePageStub => UpdatePageStub(bp, p),
                PatchOps.UpsertSurface => UpsertSurface(bp, p),
                PatchOps.ComposePage => ComposePage(bp, p),
                PatchOps.SetLanding => SetLanding(bp, p),

                PatchOps.AddConcept => Add(bp, p, bp.Concepts, Val<ConceptSpec>(p), c => c.Id,
                    list => bp with { Concepts = list }),
                PatchOps.UpdateConcept => Update(bp, p, bp.Concepts, Val<ConceptSpec>(p), c => c.Id,
                    list => bp with { Concepts = list }),
                PatchOps.RemoveConcept => Remove(bp, p, bp.Concepts, c => c.Id,
                    list => bp with { Concepts = list }),

                PatchOps.AddExternal => Add(bp, p, bp.Externals, Val<ExternalConceptSpec>(p), x => x.Id,
                    list => bp with { Externals = list }),
                PatchOps.RemoveExternal => Remove(bp, p, bp.Externals, x => x.Id,
                    list => bp with { Externals = list }),

                PatchOps.AddRelationship => Add(bp, p, bp.Relationships, Val<RelationshipSpec>(p), r => r.Id,
                    list => bp with { Relationships = list }),
                PatchOps.UpdateRelationship => Update(bp, p, bp.Relationships, Val<RelationshipSpec>(p), r => r.Id,
                    list => bp with { Relationships = list }),
                PatchOps.RemoveRelationship => Remove(bp, p, bp.Relationships, r => r.Id,
                    list => bp with { Relationships = list }),

                PatchOps.AddReference => Add(bp, p, bp.References, Val<ReferenceFieldSpec>(p), r => r.Id,
                    list => bp with { References = list }),
                PatchOps.RemoveReference => Remove(bp, p, bp.References, r => r.Id,
                    list => bp with { References = list }),

                PatchOps.AddField => AddField(bp, p),
                PatchOps.UpdateField => UpdateField(bp, p),
                PatchOps.RemoveField => RemoveField(bp, p),

                PatchOps.AddWorkflow => Add(bp, p, bp.Workflows, Val<WorkflowSpec>(p), w => w.Id,
                    list => bp with { Workflows = list }),
                PatchOps.UpdateWorkflow => Update(bp, p, bp.Workflows, Val<WorkflowSpec>(p), w => w.Id,
                    list => bp with { Workflows = list }),
                PatchOps.RemoveWorkflow => Remove(bp, p, bp.Workflows, w => w.Id,
                    list => bp with { Workflows = list }),

                PatchOps.AddActor => Add(bp, p, bp.Actors, Val<ActorSpec>(p), a => a.Id,
                    list => bp with { Actors = list }),
                PatchOps.UpdateActor => Update(bp, p, bp.Actors, Val<ActorSpec>(p), a => a.Id,
                    list => bp with { Actors = list }),
                PatchOps.RemoveActor => Remove(bp, p, bp.Actors, a => a.Id,
                    list => bp with { Actors = list }),

                PatchOps.AddJob => Add(bp, p, bp.Jobs, Val<JobSpec>(p), j => j.Id,
                    list => bp with { Jobs = list }),
                PatchOps.UpdateJob => Update(bp, p, bp.Jobs, Val<JobSpec>(p), j => j.Id,
                    list => bp with { Jobs = list }),
                PatchOps.RemoveJob => Remove(bp, p, bp.Jobs, j => j.Id,
                    list => bp with { Jobs = list }),

                PatchOps.AddScenario => Add(bp, p, bp.Scenarios, Val<ScenarioSpec>(p), s => s.Id,
                    list => bp with { Scenarios = list }),
                PatchOps.UpdateScenario => Update(bp, p, bp.Scenarios, Val<ScenarioSpec>(p), s => s.Id,
                    list => bp with { Scenarios = list }),
                PatchOps.RemoveScenario => Remove(bp, p, bp.Scenarios, s => s.Id,
                    list => bp with { Scenarios = list }),

                PatchOps.AddRequirement => Add(bp, p, bp.Ledger, Val<Requirement>(p), r => r.Id,
                    list => bp with { Ledger = list }),
                PatchOps.UpdateRequirement => Update(bp, p, bp.Ledger, Val<Requirement>(p), r => r.Id,
                    list => bp with { Ledger = list }),
                PatchOps.RemoveRequirement => Remove(bp, p, bp.Ledger, r => r.Id,
                    list => bp with { Ledger = list }),

                PatchOps.AddUncertainty => Add(bp, p, bp.Uncertainties, Val<Uncertainty>(p), u => u.Id,
                    list => bp with { Uncertainties = list }),
                PatchOps.ResolveUncertainty => ResolveUncertainty(bp, p),

                PatchOps.AddAssumption => Add(bp, p, bp.Assumptions, Val<Assumption>(p), a => a.Id,
                    list => bp with { Assumptions = list }),
                PatchOps.AddUnsupported => Add(bp, p, bp.Unsupported, Val<UnsupportedItem>(p), u => u.Id,
                    list => bp with { Unsupported = list }),

                _ => (bp, $"PATCH: '{p.Op}' is not an operation this blueprint understands"),
            };
        }
        catch (JsonException e)
        {
            return (bp, $"PATCH: '{p.Op}' carried a value that is not a valid {PatchOps.LayerOf(p.Op)} spec ({e.Message})");
        }
        catch (InvalidOperationException e)
        {
            return (bp, $"PATCH: '{p.Op}' — {e.Message}");
        }
    }

    private static string Str(SpecPatch p) =>
        p.Value?.ValueKind == JsonValueKind.String
            ? p.Value.Value.GetString() ?? ""
            : throw new InvalidOperationException("expected a text value");

    private static T Val<T>(SpecPatch p) =>
        p.Value is { } v
            ? v.Deserialize<T>(BlueprintJson.Options)
              ?? throw new InvalidOperationException("the value was null")
            : throw new InvalidOperationException("the operation needs a value and none was supplied");

    private static (Blueprint, string?) Add<T>(Blueprint bp, SpecPatch p, IReadOnlyList<T> current,
        T item, Func<T, string> id, Func<IReadOnlyList<T>, Blueprint> rebuild)
    {
        if (current.Any(x => id(x) == id(item)))
            return (bp, $"PATCH: '{p.Op}' adds '{id(item)}', which already exists");
        return (rebuild([.. current, item]), null);
    }

    private static (Blueprint, string?) Update<T>(Blueprint bp, SpecPatch p, IReadOnlyList<T> current,
        T item, Func<T, string> id, Func<IReadOnlyList<T>, Blueprint> rebuild)
    {
        // The id is the identity, so an update that changes it is really a delete plus an add —
        // exactly the confusion stable ids exist to prevent.
        var target = p.TargetId ?? id(item);
        if (id(item) != target)
            return (bp, $"PATCH: '{p.Op}' targets '{target}' but carries '{id(item)}' — an id never changes");
        var index = IndexOf(current, id, target);
        if (index < 0) return (bp, $"PATCH: '{p.Op}' targets '{target}', which does not exist");
        var list = current.ToList();
        list[index] = item;
        return (rebuild(list), null);
    }

    private static (Blueprint, string?) Remove<T>(Blueprint bp, SpecPatch p, IReadOnlyList<T> current,
        Func<T, string> id, Func<IReadOnlyList<T>, Blueprint> rebuild)
    {
        if (p.TargetId is not { } target)
            return (bp, $"PATCH: '{p.Op}' names nothing to remove");
        var index = IndexOf(current, id, target);
        if (index < 0) return (bp, $"PATCH: '{p.Op}' targets '{target}', which does not exist");
        var list = current.ToList();
        list.RemoveAt(index);
        return (rebuild(list), null);
    }

    private static int IndexOf<T>(IReadOnlyList<T> list, Func<T, string> id, string target)
    {
        for (var i = 0; i < list.Count; i++) if (id(list[i]) == target) return i;
        return -1;
    }

    // Fields live inside DataSpec, so they need their own three rather than the generic helpers.

    private static (Blueprint, string?) AddField(Blueprint bp, SpecPatch p)
    {
        var field = Val<FieldSpec>(p);
        var fields = bp.Data?.Fields.ToList() ?? [];
        if (fields.Any(f => f.Id == field.Id))
            return (bp, $"PATCH: 'add_field' adds '{field.Id}', which already exists");
        fields.Add(field);
        return (bp with { Data = new DataSpec { Fields = fields } }, null);
    }

    private static (Blueprint, string?) UpdateField(Blueprint bp, SpecPatch p)
    {
        var field = Val<FieldSpec>(p);
        var target = p.TargetId ?? field.Id;
        if (field.Id != target)
            return (bp, $"PATCH: 'update_field' targets '{target}' but carries '{field.Id}' — an id never changes");
        var fields = bp.Data?.Fields.ToList() ?? [];
        var index = IndexOf(fields, f => f.Id, target);
        if (index < 0) return (bp, $"PATCH: 'update_field' targets '{target}', which does not exist");
        fields[index] = field;
        return (bp with { Data = new DataSpec { Fields = fields } }, null);
    }

    private static (Blueprint, string?) RemoveField(Blueprint bp, SpecPatch p)
    {
        if (p.TargetId is not { } target)
            return (bp, "PATCH: 'remove_field' names nothing to remove");
        var fields = bp.Data?.Fields.ToList() ?? [];
        var index = IndexOf(fields, f => f.Id, target);
        if (index < 0) return (bp, $"PATCH: 'remove_field' targets '{target}', which does not exist");
        fields.RemoveAt(index);
        return (bp with { Data = new DataSpec { Fields = fields } }, null);
    }

    private static (Blueprint, string?) AddPageStub(Blueprint bp, SpecPatch p)
    {
        var page = Val<PageSpec>(p) with { SurfaceIds = [] };
        var experience = bp.Experience ?? new ExperienceSpec();
        if (experience.Pages.Any(x => x.Id == page.Id))
            return (bp, $"PATCH: 'add_page_stub' adds '{page.Id}', which already exists");
        return (bp with { Experience = experience with { Pages = [.. experience.Pages, page] } }, null);
    }

    private static (Blueprint, string?) UpdatePageStub(Blueprint bp, SpecPatch p)
    {
        var page = Val<PageSpec>(p);
        var experience = bp.Experience ?? new ExperienceSpec();
        var pages = experience.Pages.ToList();
        var target = p.TargetId ?? page.Id;
        var index = IndexOf(pages, x => x.Id, target);
        if (index < 0) return (bp, $"PATCH: 'update_page_stub' targets '{target}', which does not exist");
        if (page.Id != target) return (bp, $"PATCH: 'update_page_stub' cannot change page id '{target}'");
        pages[index] = page with { SurfaceIds = pages[index].SurfaceIds };
        return (bp with { Experience = experience with { Pages = pages } }, null);
    }

    private static (Blueprint, string?) UpsertSurface(Blueprint bp, SpecPatch p)
    {
        var surface = Val<SurfaceSpec>(p);
        var experience = bp.Experience ?? new ExperienceSpec();
        var surfaces = experience.Surfaces.ToList();
        var index = IndexOf(surfaces, x => x.Id, surface.Id);
        if (index < 0) surfaces.Add(surface); else surfaces[index] = surface;
        return (bp with { Experience = experience with { Surfaces = surfaces } }, null);
    }

    private static (Blueprint, string?) ComposePage(Blueprint bp, SpecPatch p)
    {
        var composition = Val<PageCompositionSpec>(p);
        var experience = bp.Experience ?? new ExperienceSpec();
        var pages = experience.Pages.ToList();
        var index = IndexOf(pages, x => x.Id, composition.PageId);
        if (index < 0) return (bp, $"PATCH: 'compose_page' targets '{composition.PageId}', which does not exist");
        pages[index] = pages[index] with { SurfaceIds = composition.SurfaceIds };
        return (bp with { Experience = experience with { Pages = pages } }, null);
    }

    private static (Blueprint, string?) SetLanding(Blueprint bp, SpecPatch p)
    {
        var landing = Val<IReadOnlyList<LandingSpec>>(p);
        var experience = bp.Experience ?? new ExperienceSpec();
        return (bp with { Experience = experience with { Landing = landing } }, null);
    }

    /// <summary>Answering a question is a patch like any other, so the answer is recorded WITH the
    /// revision it landed in. That is what lets the ledger say a requirement came from a user
    /// answer rather than from a model's guess.</summary>
    private static (Blueprint, string?) ResolveUncertainty(Blueprint bp, SpecPatch p)
    {
        if (p.TargetId is not { } target)
            return (bp, "PATCH: 'resolve_uncertainty' names no question");
        var list = bp.Uncertainties.ToList();
        var index = IndexOf(list, u => u.Id, target);
        if (index < 0) return (bp, $"PATCH: 'resolve_uncertainty' targets '{target}', which does not exist");

        var resolution = Val<UncertaintyResolution>(p);
        list[index] = list[index] with { Resolution = resolution with { Revision = bp.Revision + 1 } };
        return (bp with { Uncertainties = list }, null);
    }
}

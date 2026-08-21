// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cordango.Definition.Blueprints;

/// <summary>
/// One interview role: a narrow job, a tool schema covering exactly its own layer, and the patch
/// operation its output becomes.
/// </summary>
/// <param name="Key">Stable role key, used in prompts, cost records and diagnostics.</param>
/// <param name="Layer">The spec layer it may touch, or null for a role whose output is bookkeeping
/// about the blueprint rather than part of it — an uncertainty names the layer it affects on itself,
/// so the planner that raises it does not belong to one.</param>
/// <param name="ToolName">The forced tool call name.</param>
/// <param name="SchemaDef">The <c>$defs</c> entry in <c>blueprint.schema.json</c> it emits.</param>
/// <param name="Property">The array property inside the tool call, for list-emitting roles.</param>
/// <param name="AddOp">The patch operation each emitted item becomes.</param>
/// <param name="Singular">True when the role emits ONE object rather than a list.</param>
public sealed record ProposerRole(
    string Key, string? Layer, string ToolName, string SchemaDef,
    string Property, string AddOp, bool Singular = false);

/// <summary>
/// The interview roles and their tool schemas.
///
/// <para><b>Why each role gets its own sliced schema.</b> A proposer handed the whole blueprint
/// schema could emit workflows and screens while it was supposed to be naming concepts — and
/// nothing downstream would know those were never reviewed. Slicing the schema to one layer makes
/// that structurally impossible rather than merely discouraged: the tool call cannot carry the
/// fields, so the model cannot smuggle them through.</para>
///
/// <para><b>Why roles emit specs and not patches.</b> A patch envelope with a free-form
/// <c>value</c> would be schema-shaped as "any object", which throws away the guarantee above. So a
/// role emits its own layer's objects under a narrow schema, and deterministic code turns them into
/// patches. Both properties hold at once: the tool call is constrained, and application is still
/// operation-by-operation against ids.</para>
/// </summary>
public static class ProposerRoles
{
    public static readonly ProposerRole Interpreter = new(
        "interpreter", SpecLayers.Intent, "emit_app_brief", "interpretationSpec",
        "intent", PatchOps.SetIntent, Singular: true);

    // No layer: each uncertainty names the layer it affects, so the planner that raises them
    // does not belong to one.
    public static readonly ProposerRole UncertaintyPlanner = new(
        "uncertainty_planner", null, "emit_uncertainties", "uncertainty",
        "uncertainties", PatchOps.AddUncertainty);

    public static readonly ProposerRole ConceptProposer = new(
        "concept_proposer", SpecLayers.Concepts, "emit_concepts", "conceptSpec",
        "concepts", PatchOps.AddConcept);

    public static readonly ProposerRole RelationshipProposer = new(
        "relationship_proposer", SpecLayers.Relationships, "emit_relationships", "relationshipSpec",
        "relationships", PatchOps.AddRelationship);

    public static readonly ProposerRole WorkflowProposer = new(
        "workflow_proposer", SpecLayers.Workflows, "emit_workflow", "workflowSpec",
        "workflows", PatchOps.AddWorkflow);

    public static readonly ProposerRole DataProposer = new(
        "data_proposer", SpecLayers.Data, "emit_fields", "fieldSpec",
        "fields", PatchOps.AddField);

    public static readonly ProposerRole ActorProposer = new(
        "actor_proposer", SpecLayers.Actors, "emit_actors", "actorSpec",
        "actors", PatchOps.AddActor);

    public static readonly ProposerRole JobProposer = new(
        "job_proposer", SpecLayers.Jobs, "emit_jobs", "jobSpec",
        "jobs", PatchOps.AddJob);

    public static readonly ProposerRole ExperienceProposer = new(
        "experience_proposer", SpecLayers.Experience, "emit_experience", "experienceSpec",
        "experience", PatchOps.SetExperience, Singular: true);

    public static readonly ProposerRole ScenarioProposer = new(
        "scenario_proposer", SpecLayers.Scenarios, "emit_scenarios", "scenarioSpec",
        "scenarios", PatchOps.AddScenario);

    public static readonly ProposerRole[] All =
    [
        Interpreter, UncertaintyPlanner, ConceptProposer, RelationshipProposer,
        WorkflowProposer, DataProposer, ActorProposer, JobProposer,
        ExperienceProposer, ScenarioProposer,
    ];

    public static ProposerRole? Find(string key) =>
        All.FirstOrDefault(r => r.Key == key);

    /// <summary>
    /// The forced-tool input schema for a role: its own layer, and nothing else — then rewritten so
    /// the role cannot mint a field id.
    ///
    /// <para>Three passes name the same field: the concept pass names the one that titles a record,
    /// the workflow pass names the one a transition requires, and the data pass creates it. They are
    /// separate model calls that cannot see each other's choices, so every id is an independent
    /// guess that has to coincide with two others. It does not, and the result is a gate error about
    /// a field belonging to another concept or not existing at all.</para>
    ///
    /// <para>So a proposer says which field it means by KEY, in the context that already names the
    /// owning concept, and <see cref="SpecIds.Field"/> does the naming. Removing the property from
    /// the schema is what makes this structural: the tool call cannot carry an id, so no wording is
    /// relied on. See <see cref="SpecIds"/> for the full argument.</para>
    /// </summary>
    public static JsonNode ToolSchema(ProposerRole role)
    {
        var schema = role.Singular
            ? Schemas.BlueprintSliceSchema(role.SchemaDef)
            : Schemas.BlueprintListSchema(role.SchemaDef, role.Property);
        return AsProposal(role, schema);
    }

    private static JsonNode AsProposal(ProposerRole role, JsonNode schema)
    {
        if (schema["$defs"] is not JsonObject defs) return schema;

        // The data pass supplies conceptId and key, which already identify the field uniquely.
        if (defs["fieldSpec"] is JsonObject field) Drop(field, "id");

        // "records are titled by the field keyed 'title'" — on THIS concept, so it can no longer
        // name a field that turns out to live on another one.
        if (At(defs, "conceptSpec", "properties", "recordLabel") is JsonObject label)
            Rename(label, "fieldId", "fieldKey",
                "The KEY of the field that titles a record of this concept — 'title', 'name', "
                + "'subject'. Not an id: the field does not exist yet, and this is what makes the "
                + "data pass create it, on this concept.");

        if (At(defs, "workflowSpec", "properties", "transitions", "items") is JsonObject transition)
            Rename(transition, "requiresFieldIds", "requiresFieldKeys",
                "KEYS of fields on the concept this workflow governs that must be filled before the "
                + "move is legal. They need not exist yet — naming one here is what creates it.");

        if (At(defs, "workflowSpec", "properties", "actions", "items") is JsonObject action)
            Rename(action, "inputFieldIds", "inputFieldKeys",
                "KEYS of fields on the concept this workflow governs that this action collects when "
                + "it runs. They need not exist yet.");

        // A formula and a rollup name fields too, and with fieldSpec.id gone the proposer has no way
        // to know the ids it is creating — so these have to speak keys or every formula would point
        // at nothing.
        if (At(defs, "fieldSpec", "properties", "computed", "properties") is JsonObject computed)
        {
            if (computed["expr"] is JsonObject expr)
                expr["description"] = "Typed expression over this record, written with field KEYS. "
                    + "Arithmetic (+ - * /) returns numbers; numeric or date comparisons (< <= > >= == !=) and "
                    + "boolean logic (and/or/not) return booleans. Expressions may read other "
                    + "computed fields when there is no cycle. Example: 'available < minimum'. Not ids.";

            if (computed["rollup"] is JsonObject rollup)
                Rename(rollup, "fieldId", "fieldKey",
                    "The KEY of the field being aggregated, on the concept named by conceptId.");
        }

        return schema;
    }

    private static JsonNode? At(JsonNode? node, params string[] path)
    {
        foreach (var step in path)
        {
            if (node is not JsonObject o || o[step] is not { } next) return null;
            node = next;
        }
        return node;
    }

    private static void Drop(JsonObject def, string property)
    {
        (def["properties"] as JsonObject)?.Remove(property);
        if (def["required"] is JsonArray required)
            for (var i = required.Count - 1; i >= 0; i--)
                if (required[i]?.GetValue<string>() == property) required.RemoveAt(i);
    }

    private static void Rename(JsonObject def, string from, string to, string description)
    {
        if (def["properties"] is not JsonObject props || props[from] is not JsonNode shape) return;
        props.Remove(from);

        // The stored form is an id with an fld_ prefix; the proposed form is a bare key, so the id
        // pattern has to go with it or every valid answer would be refused.
        var next = shape.DeepClone();
        Depattern(next);
        if (next is JsonObject o) o["description"] = description;
        props[to] = next;

        if (def["required"] is JsonArray required)
            for (var i = 0; i < required.Count; i++)
                if (required[i]?.GetValue<string>() == from) required[i] = to;
    }

    private static void Depattern(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                o.Remove("pattern");
                o.Remove("$ref");
                if (!o.ContainsKey("type") && !o.ContainsKey("items")) o["type"] = "string";
                foreach (var child in o.ToList()) Depattern(child.Value);
                break;
            case JsonArray a:
                foreach (var child in a) Depattern(child);
                break;
        }
    }

    /// <summary>What a proposal became, and what was quietly not part of it.</summary>
    /// <param name="Patches">The operations to apply.</param>
    /// <param name="Notes">Things the translation decided, in the user's terms. Not errors — a note
    /// says something the user should know happened, like a proposed field being dropped because
    /// another layer already owns it.</param>
    public sealed record ProposalPatches(IReadOnlyList<SpecPatch> Patches, IReadOnlyList<string> Notes)
    {
        public int Count => Patches.Count;
    }

    /// <summary>
    /// Turns a role's tool output into patches. Deterministic: the role decided WHAT, this decides
    /// nothing beyond which operation carries it — and the ids, which are derived rather than
    /// decided (see <see cref="SpecIds"/>).
    /// </summary>
    public static ProposalPatches ToPatches(ProposerRole role, JsonNode? output, Blueprint blueprint,
        int baseRevision, string provenance = FieldProvenance.ModelSuggested, bool replaceExisting = false)
    {
        if (output is not JsonObject obj) return new ProposalPatches([], []);

        var notes = new List<string>();
        // The interpreter reads INTENT and nothing else. It used to emit the app's identity too, and
        // that was the wrong division of labour: the name and the web address are the user's, given
        // before any model runs, and a proposal that carries them can overwrite a choice somebody
        // already made. It also created failures nobody could act on — a derived handle that collided
        // with an existing app killed the whole step, repeatedly, after the call had been paid for.
        if (role.Key == Interpreter.Key)
        {
            if (obj["intent"] is not JsonObject intent) return new ProposalPatches([], notes);

            return new ProposalPatches(
            [
                new SpecPatch(PatchOps.SetIntent,
                    Value: JsonSerializer.Deserialize<JsonElement>(intent.ToJsonString()),
                    Provenance: provenance, BaseRevision: baseRevision),
            ], notes);
        }

        if (role.Singular)
        {
            if (role == ExperienceProposer)
            {
                var experience = obj.Deserialize<ExperienceSpec>(BlueprintJson.Options);
                if (experience is null) return new ProposalPatches([], []);
                var experiencePatches = new List<SpecPatch>();
                experiencePatches.AddRange(experience.Surfaces.Select(surface => new SpecPatch(
                    PatchOps.UpsertSurface,
                    Value: JsonSerializer.SerializeToElement(surface, BlueprintJson.Options),
                    Provenance: provenance, BaseRevision: baseRevision)));
                foreach (var page in experience.Pages)
                {
                    var exists = blueprint.Experience?.Pages.Any(p => p.Id == page.Id) == true;
                    experiencePatches.Add(new SpecPatch(exists ? PatchOps.UpdatePageStub : PatchOps.AddPageStub,
                        TargetId: exists ? page.Id : null,
                        Value: JsonSerializer.SerializeToElement(page, BlueprintJson.Options),
                        Provenance: provenance, BaseRevision: baseRevision));
                    experiencePatches.Add(new SpecPatch(PatchOps.ComposePage,
                        TargetId: page.Id,
                        Value: JsonSerializer.SerializeToElement(new PageCompositionSpec
                            { PageId = page.Id, SurfaceIds = page.SurfaceIds }, BlueprintJson.Options),
                        Provenance: provenance, BaseRevision: baseRevision));
                }
                experiencePatches.Add(new SpecPatch(PatchOps.SetLanding,
                    Value: JsonSerializer.SerializeToElement(experience.Landing, BlueprintJson.Options),
                    Provenance: provenance, BaseRevision: baseRevision));
                return new ProposalPatches(experiencePatches, notes);
            }
            // A singular role's tool schema IS the spec, so the whole payload is the value.
            var value = JsonSerializer.Deserialize<JsonElement>(obj.ToJsonString());
            return new ProposalPatches(
                [new SpecPatch(role.AddOp, Value: value, Provenance: provenance, BaseRevision: baseRevision)],
                notes);
        }

        if (obj[role.Property] is not JsonArray items) return new ProposalPatches([], []);

        var patches = new List<SpecPatch>();
        foreach (var item in items)
        {
            if (item is not JsonObject spec) continue;
            if (!Resolve(role, spec, blueprint, notes)) continue;
            var value = JsonSerializer.Deserialize<JsonElement>(spec.ToJsonString());
            var op = replaceExisting ? ExistingOp(role, spec, blueprint) ?? role.AddOp : role.AddOp;
            patches.Add(new SpecPatch(op,
                TargetId: op == role.AddOp ? null : spec["id"]?.GetValue<string>(),
                Value: value, Provenance: provenance,
                BaseRevision: baseRevision));

            if (role.Key == "workflow_proposer")
                patches.AddRange(StatusField(spec, blueprint, baseRevision));
        }
        return new ProposalPatches(patches, notes);
    }

    private static string? ExistingOp(ProposerRole role, JsonObject spec, Blueprint bp)
    {
        var id = spec["id"]?.GetValue<string>();
        if (id is null) return null;
        return role.Key switch
        {
            "concept_proposer" when bp.Concepts.Any(x => x.Id == id) => PatchOps.UpdateConcept,
            "relationship_proposer" when bp.Relationships.Any(x => x.Id == id) => PatchOps.UpdateRelationship,
            "workflow_proposer" when bp.Workflows.Any(x => x.Id == id) => PatchOps.UpdateWorkflow,
            "data_proposer" when bp.AllFields.Any(x => x.Id == id) => PatchOps.UpdateField,
            "actor_proposer" when bp.Actors.Any(x => x.Id == id) => PatchOps.UpdateActor,
            "job_proposer" when bp.Jobs.Any(x => x.Id == id) => PatchOps.UpdateJob,
            "scenario_proposer" when bp.Scenarios.Any(x => x.Id == id) => PatchOps.UpdateScenario,
            _ => null,
        };
    }

    /// <summary>
    /// The field a lifecycle governs, created alongside the lifecycle that owns it.
    ///
    /// <para>Nothing else can create it: the data pass is refused (its states are the options, and a
    /// second declaration fights the process for the column), and lowering runs long after the
    /// screens are designed. But a board groups by it, a filter bar facets on it and a metric counts
    /// by it — so if it does not exist as a field the whole experience layer points at nothing, which
    /// is exactly what "board groups by fld_order_status, which is not a field" was.</para>
    ///
    /// <para>Derived rather than proposed, like every other field id: the workflow already decided
    /// the key, and its label is the name the user approved for the lifecycle.</para>
    /// </summary>
    private static IEnumerable<SpecPatch> StatusField(JsonObject workflow, Blueprint bp, int baseRevision)
    {
        var concept = bp.Concept(workflow["conceptId"]?.GetValue<string>() ?? "");
        if (concept is null) yield break;
        if (workflow["statusFieldKey"]?.GetValue<string>() is not { Length: > 0 } key) yield break;

        var id = SpecIds.Field(concept.Key, key);
        if (bp.AllFields.Any(f => f.Id == id || (f.ConceptId == concept.Id && f.Key == key))) yield break;

        var field = new JsonObject
        {
            ["id"] = id,
            ["key"] = key,
            ["conceptId"] = concept.Id,
            ["label"] = workflow["label"]?.GetValue<string>() ?? "Status",
            ["type"] = FieldTypes.Select,
            ["purpose"] = FieldPurposes.Transition,
            // Not a guess and not the user's request either: the lifecycle they approved is what
            // requires it, and provenance is a fact about where something came from.
            ["provenance"] = FieldProvenance.DerivedFromWorkflow,
            ["reason"] = "Where a record is up to. The lifecycle's stages are its values.",
        };

        yield return new SpecPatch(PatchOps.AddField,
            Value: JsonSerializer.Deserialize<JsonElement>(field.ToJsonString()),
            Provenance: FieldProvenance.DerivedFromWorkflow, BaseRevision: baseRevision);
    }

    /// <summary>
    /// Turns the keys a proposer supplied into the ids the blueprint stores. Returns false when the
    /// item should not become a patch at all.
    /// </summary>
    private static bool Resolve(ProposerRole role, JsonObject spec, Blueprint bp, List<string> notes)
    {
        switch (role.Key)
        {
            case "concept_proposer":
            {
                // The concept is in this very payload, so its key is right here — which is exactly
                // why the record label can no longer end up on somebody else's concept.
                var key = spec["key"]?.GetValue<string>() ?? "";
                if (spec["recordLabel"] is JsonObject label && Take(label, "fieldKey") is { } fieldKey)
                    label["fieldId"] = SpecIds.Field(key, fieldKey);
                return true;
            }

            case "data_proposer":
            {
                var concept = bp.Concept(spec["conceptId"]?.GetValue<string>() ?? "");
                if (concept is null) return true;   // the gate reports the dangling conceptId
                var key = spec["key"]?.GetValue<string>() ?? "";
                spec["id"] = SpecIds.Field(concept.Key, key);

                // The status field is BUILT from the lifecycle — its states are its options, and the
                // workflow pass already created it. A data proposal for it is out of layer in the
                // same way a screen would be, so it is dropped, and said out loud rather than
                // swallowed.
                if (bp.Workflows.Any(w => w.ConceptId == concept.Id && w.StatusFieldKey == key))
                {
                    notes.Add($"'{spec["label"]?.GetValue<string>() ?? key}' was left out: "
                        + $"{concept.Name} already has a lifecycle, and that is what fills this field in");
                    return false;
                }

                Computed(spec, concept, bp, notes);
                return true;
            }

            case "workflow_proposer":
            {
                var concept = bp.Concept(spec["conceptId"]?.GetValue<string>() ?? "");
                if (concept is null) return true;
                foreach (var t in spec["transitions"] as JsonArray ?? [])
                    if (t is JsonObject transition)
                        Ids(transition, "requiresFieldKeys", "requiresFieldIds", concept.Key);
                foreach (var a in spec["actions"] as JsonArray ?? [])
                    if (a is JsonObject action)
                        Ids(action, "inputFieldKeys", "inputFieldIds", concept.Key);
                return true;
            }

            case "experience_proposer" or "actor_proposer" or "job_proposer":
                // These reference things that already exist and are shown to them by id. Nothing to
                // derive — the digest is what has to be right, not the translation.
                return true;

            default:
                return true;
        }
    }

    /// <summary>Rewrites a list of field keys into ids on the workflow's own concept. A transition
    /// can no longer require a field belonging to somewhere else, because there is nowhere else it
    /// could name.</summary>
    private static void Ids(JsonObject owner, string from, string to, string conceptKey)
    {
        if (owner[from] is not JsonArray keys) return;
        owner.Remove(from);
        var ids = new JsonArray();
        foreach (var k in keys)
            if (k?.GetValue<string>() is { Length: > 0 } key)
                ids.Add(SpecIds.Field(conceptKey, key));
        owner[to] = ids;
    }

    /// <summary>Turns the keys in a formula into field ids. An expression reads only its OWN record
    /// (a gate rule), so every name in it is a key on this field's concept — which is what makes the
    /// substitution safe rather than a guess. A name followed by '(' is a function, not a field.</summary>
    private static void Computed(JsonObject field, ConceptSpec concept, Blueprint bp, List<string> notes)
    {
        if (field["computed"] is not JsonObject computed) return;

        if (computed["expr"]?.GetValue<string>() is { Length: > 0 } expr)
            computed["expr"] = Substitute(expr, concept.Key);

        if (computed["rollup"] is not JsonObject rollup) return;

        // A rollup already names the records being aggregated and the record receiving the result.
        // If the model picked an unrelated relationship but exactly one relationship connects that
        // pair in the required direction, selecting it is deterministic rather than a product guess.
        var over = bp.Concept(rollup["conceptId"]?.GetValue<string>() ?? "");
        var via = bp.Relationship(rollup["viaRelationshipId"]?.GetValue<string>() ?? "");
        if (over is not null && (via is null || via.OwningConceptId != over.Id || via.TargetConceptId != concept.Id))
        {
            var candidates = bp.Relationships
                .Where(r => r.OwningConceptId == over.Id && r.TargetConceptId == concept.Id)
                .ToList();
            if (candidates.Count == 1)
            {
                rollup["viaRelationshipId"] = candidates[0].Id;
                notes.Add($"'{field["label"]?.GetValue<string>() ?? field["key"]?.GetValue<string>()}' "
                    + $"was connected through '{candidates[0].Label}', the relationship from "
                    + $"{over.Name} to {concept.Name}");
            }
        }

        if (Take(rollup, "fieldKey") is { } aggregated)
            // The aggregated field belongs to the concept being rolled UP, not to this one.
            rollup["fieldId"] = SpecIds.Field(over?.Key ?? concept.Key, aggregated);
    }

    private static string Substitute(string expr, string conceptKey)
    {
        var sb = new System.Text.StringBuilder(expr.Length);
        var i = 0;
        while (i < expr.Length)
        {
            if (!char.IsAsciiLetter(expr[i]) && expr[i] != '_') { sb.Append(expr[i++]); continue; }

            var start = i;
            while (i < expr.Length && (char.IsAsciiLetterOrDigit(expr[i]) || expr[i] == '_')) i++;
            var name = expr[start..i];

            var j = i;
            while (j < expr.Length && expr[j] == ' ') j++;
            var isCall = j < expr.Length && expr[j] == '(';

            sb.Append(isCall || ComputedExpr.Keywords.Contains(name) || name.StartsWith("fld_", StringComparison.Ordinal)
                ? name
                : SpecIds.Field(conceptKey, name));
        }
        return sb.ToString();
    }

    private static string? Take(JsonObject obj, string property)
    {
        if (obj[property]?.GetValue<string>() is not { Length: > 0 } value) return null;
        obj.Remove(property);
        return value;
    }
}

// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json;

namespace Cordango.Definition.Blueprints;

/// <summary>
/// Collects gate findings in two classes, because a blueprint has two different ways of being
/// unacceptable and only one of them is a mistake.
///
/// <para><b>Contradiction</b> — two things that both exist disagree, or a value is invalid on its
/// face. Adding more content can never fix it; something has to change or go. An enumeration with
/// fields, a composition that is many-to-many, two concepts sharing a key.</para>
///
/// <para><b>Gap</b> — something a finished app needs is absent, including a reference to an element
/// nobody has authored YET. Adding content fixes it. A concept with no fields, a board with nothing
/// to group by, a record label naming a field the data pass has not reached.</para>
///
/// <para>The distinction exists because the interview builds a blueprint one layer at a time. After
/// the concept pass, every concept legitimately has no fields — the data pass is four calls away. A
/// gate that cannot tell "not finished" from "wrong" rejects every proposal for gaps its own
/// sequencing created, and reports them against a layer the proposal never touched.</para>
/// </summary>
internal sealed class GateErrors
{
    private readonly List<string> _contradictions = [];
    private readonly List<string> _gaps = [];

    public void Wrong(string message) => _contradictions.Add("SEMANTIC: " + message);
    public void Missing(string message) => _gaps.Add("INCOMPLETE: " + message);

    /// <summary>Only the things that are actually wrong. What a draft has to satisfy.</summary>
    public List<string> Contradictions => [.. _contradictions];

    /// <summary>Only the things that are unfinished.</summary>
    public List<string> Gaps => [.. _gaps];

    /// <summary>Everything. What approval has to satisfy.</summary>
    public List<string> All => [.. _contradictions, .. _gaps];
}

/// <summary>
/// Validates a blueprint BEFORE anything is lowered. Two jobs, in order: structural conformance to
/// <c>blueprint.schema.json</c>, then the semantics a JSON Schema cannot express.
///
/// <para>The important half is the second one, and specifically the <b>surface → data contracts</b>.
/// A user who approved a calendar and a data spec with no date field has approved something that
/// cannot be built. Catching that here makes it a question at approval time, when the answer is
/// cheap; catching it after generation makes it a broken screen, and the user reasonably concludes
/// the whole thing does not work.</para>
///
/// <para>Two entry points, and the difference matters. <see cref="Validate"/> is the approval gate:
/// everything must hold. <see cref="DraftErrors"/> is what a mid-interview document has to satisfy —
/// self-consistency only, because the layers arrive in sequence and an unfinished blueprint is the
/// normal state of one. See <see cref="GateErrors"/> for where the line falls.</para>
///
/// <para>Errors are deliberately non-cascading: when a reference does not resolve, it is reported
/// once and every check that depended on it is skipped. A single mistake producing fifteen errors
/// is how a validator becomes something people stop reading.</para>
/// </summary>
public static class BlueprintGate
{
    /// <summary>The approval gate: structure, contradictions and gaps. Nothing lowers until this is
    /// empty.</summary>
    public static List<string> Validate(Blueprint bp)
    {
        var errors = StructuralErrors(bp);
        // A document that does not match its own schema cannot be reasoned about semantically —
        // every check below assumes the shapes are what they claim to be.
        if (errors.Count > 0) return errors;
        errors.AddRange(Collect(bp).All);
        return errors;
    }

    /// <summary>
    /// What a blueprint has to satisfy WHILE it is being built: it must not contradict itself.
    ///
    /// <para>Applied after every patch, so a proposal is rejected the moment it disagrees with a
    /// decision already in the document — but not merely because a later layer has not run.</para>
    /// </summary>
    public static List<string> DraftErrors(Blueprint bp)
    {
        var errors = StructuralErrors(bp);
        if (errors.Count > 0) return errors;
        errors.AddRange(Collect(bp).Contradictions);
        return errors;
    }

    public static List<string> StructuralErrors(Blueprint bp) =>
        Gate.StructuralErrors(BlueprintJson.ToNode(bp), Schemas.BlueprintSchemaNode());

    public static List<string> SemanticErrors(Blueprint bp) => Collect(bp).All;

    /// <summary>The gaps only — what is unfinished rather than wrong. Used to explain to a user why
    /// approval is not yet available without burying it among real errors.</summary>
    public static List<string> IncompleteErrors(Blueprint bp) => Collect(bp).Gaps;

    private static GateErrors Collect(Blueprint bp)
    {
        var e = new GateErrors();
        Identity(bp, e);
        Actors(bp, e);
        Concepts(bp, e);
        Externals(bp, e);
        Relationships(bp, e);
        References(bp, e);
        Fields(bp, e);
        Workflows(bp, e);
        Jobs(bp, e);
        Experience(bp, e);
        Scenarios(bp, e);
        Ledger(bp, e);
        return e;
    }

    // ---- identity ---------------------------------------------------------------------------

    /// <summary>Ids are the spine of the whole program: rename tracking, migration planning and the
    /// requirement ledger all key off them. A duplicate id is not a cosmetic problem — it makes two
    /// different things indistinguishable forever after.</summary>
    private static void Identity(Blueprint bp, GateErrors e)
    {
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        void Claim(string id, string what)
        {
            if (string.IsNullOrEmpty(id)) return;
            if (seen.TryGetValue(id, out var first))
                e.Wrong($"id '{id}' is used by both {first} and {what} — ids are unique across the whole blueprint");
            else seen[id] = what;
        }

        foreach (var a in bp.Actors) Claim(a.Id, $"actor '{a.Name}'");
        foreach (var c in bp.Concepts) Claim(c.Id, $"concept '{c.Name}'");
        foreach (var x in bp.Externals) Claim(x.Id, $"external '{x.Name}'");
        foreach (var r in bp.Relationships) Claim(r.Id, $"relationship '{r.Label}'");
        foreach (var r in bp.References) Claim(r.Id, $"reference '{r.Label}'");
        foreach (var f in bp.AllFields) Claim(f.Id, $"field '{f.Label}'");
        foreach (var w in bp.Workflows)
        {
            Claim(w.Id, $"workflow '{w.Label}'");
            foreach (var s in w.States) Claim(s.Id, $"state '{s.Label}'");
            foreach (var t in w.Transitions) Claim(t.Id, $"transition '{t.Label}'");
            foreach (var a in w.Actions) Claim(a.Id, $"action '{a.Label}'");
        }
        foreach (var j in bp.Jobs) Claim(j.Id, $"job '{j.Task}'");
        foreach (var s in bp.Experience?.Surfaces ?? []) Claim(s.Id, $"surface '{s.Label}'");
        foreach (var p in bp.Experience?.Pages ?? []) Claim(p.Id, $"page '{p.Label}'");
        foreach (var s in bp.Scenarios) Claim(s.Id, $"scenario '{s.Title}'");
        foreach (var u in bp.Uncertainties) Claim(u.Id, "uncertainty");

        Duplicates(bp.Concepts.Where(c => ConceptKinds.ProducesEntity(c.Kind)).Select(c => c.Key),
            k => e.Wrong($"two concepts share the key '{k}' — keys become entity keys and must be unique"));
        Duplicates(bp.Actors.Select(a => a.Key),
            k => e.Wrong($"two actors share the key '{k}'"));
        Duplicates((bp.Experience?.Pages ?? []).Select(p => p.Key),
            k => e.Wrong($"two pages share the key '{k}'"));
        Duplicates((bp.Experience?.Surfaces ?? []).Select(s => s.Key),
            k => e.Wrong($"two surfaces share the key '{k}'"));
    }

    private static void Duplicates(IEnumerable<string> keys, Action<string> report)
    {
        foreach (var g in keys.Where(k => !string.IsNullOrEmpty(k))
                     .GroupBy(k => k, StringComparer.Ordinal).Where(g => g.Count() > 1))
            report(g.Key);
    }

    // ---- actors -----------------------------------------------------------------------------

    /// <summary>Permissions are the half of an actor that lowers. If they name things that do not
    /// exist, or things that store nothing, the lowered app gives everyone the same access and every
    /// multi-role scenario passes for the wrong reason.</summary>
    private static void Actors(Blueprint bp, GateErrors e)
    {
        foreach (var a in bp.Actors)
        {
            foreach (var p in a.EntityPermissions)
            {
                var c = bp.Concept(p.ConceptId);
                if (c is null)
                    e.Missing($"actor '{a.Name}' has permissions on '{p.ConceptId}', which is not a concept");
                else if (!ConceptKinds.ProducesEntity(c.Kind))
                    e.Wrong($"actor '{a.Name}' has permissions on '{c.Name}', which is a {c.Kind} and stores nothing. "
                        + "If this list is something people MANAGE, it is a 'config' concept, not an enumeration");
            }

            foreach (var g in a.EntityPermissions.GroupBy(p => p.ConceptId).Where(g => g.Count() > 1))
                e.Wrong($"actor '{a.Name}' has two permission entries for '{g.Key}'");

            foreach (var fp in a.FieldPermissions)
                if (bp.Field(fp.FieldId) is null)
                    e.Missing($"actor '{a.Name}' has a permission on field '{fp.FieldId}', which does not exist");

            foreach (var aid in a.ActionPermissions)
                if (bp.Workflows.SelectMany(w => w.Actions).All(x => x.Id != aid))
                    e.Missing($"actor '{a.Name}' may run action '{aid}', which no workflow declares");
        }
    }

    // ---- concepts ---------------------------------------------------------------------------

    private static void Concepts(Blueprint bp, GateErrors e)
    {
        foreach (var c in bp.Concepts)
        {
            var fields = bp.AllFields.Where(f => f.ConceptId == c.Id).ToList();

            switch (c.Kind)
            {
                case ConceptKinds.Enumeration:
                    if (c.Options.Count == 0)
                        e.Missing($"concept '{c.Name}' is an enumeration but declares no options — an enumeration IS its option list");
                    if (fields.Count > 0)
                        e.Wrong($"concept '{c.Name}' is an enumeration but has {fields.Count} field(s) — a value set with its own fields is a 'local' concept, not an enumeration");
                    Duplicates(c.Options.Select(o => o.Value),
                        v => e.Wrong($"concept '{c.Name}' declares the option value '{v}' twice"));
                    break;

                case ConceptKinds.Terminology:
                    if (fields.Count > 0)
                        e.Wrong($"concept '{c.Name}' is terminology (vocabulary only, no storage) but has fields");
                    break;

                default:
                    if (c.Options.Count > 0)
                        e.Wrong($"concept '{c.Name}' declares options but is '{c.Kind}' — only an enumeration carries options");
                    if (fields.Count == 0)
                        e.Missing($"concept '{c.Name}' has no fields — every entity needs at least one, and a concept nobody stores anything about is probably terminology or an enumeration");
                    RecordLabelRules(bp, c, fields, e);
                    break;
            }

            if (c.Kind == ConceptKinds.Subordinate)
            {
                var owning = bp.Relationships.Where(r => r.IsComposition && r.ToConceptId == c.Id).ToList();
                if (owning.Count == 0)
                    e.Missing($"concept '{c.Name}' is subordinate but no composition relationship makes it one — a subordinate record has to belong to something");
                else if (owning.Count > 1)
                    e.Wrong($"concept '{c.Name}' is subordinate to {owning.Count} parents — a record lives inside exactly one");
            }

            EntityVersusEnumeration(bp, c, e);
        }

        // Caught HERE, at the concept pass, rather than four passes later when the data pass creates
        // the field and one concept wins it. A generic id — fld_name across Engagement, Event and
        // Asset — is the natural thing to write and produces two screens whose rows are all titled
        // after a third concept's records. A field belongs to exactly one concept, so the promise has
        // to be unique the moment it is made.
        foreach (var g in bp.Concepts.Where(c => c.RecordLabel is not null)
                     .GroupBy(c => c.RecordLabel!.FieldId, StringComparer.Ordinal)
                     .Where(g => g.Count() > 1))
            e.Wrong($"{string.Join(" and ", g.Select(c => $"'{c.Name}'"))} all name '{g.Key}' as the field that "
                + "titles their records — a field belongs to one concept, so each needs its own");
    }

    /// <summary>Every browsable record needs a name on screen, and it has to be ONE field: the App
    /// Definition's <c>displayField</c> takes a field key, there is no template, and computed fields
    /// are derived values rather than record labels. Saying so here is what turns a
    /// composite label from a silently wrong screen into a recorded, visible limitation.</summary>
    private static void RecordLabelRules(Blueprint bp, ConceptSpec c, List<FieldSpec> fields, GateErrors e)
    {
        if (c.Kind == ConceptKinds.Settings) return;   // a singleton is never listed, so it needs no label
        if (c.RecordLabel is null)
        {
            // A shift assignment reads as "Picking · Early · Monday" — three fields. The App
            // Definition's displayField takes one field key, so the honest options are to record the
            // gap or to fake it. Recorded, it stays visible in the ledger and can be counted across
            // the corpus; faked, it becomes a screen where every row is called the same thing.
            var recorded = bp.Unsupported.Any(u =>
                u.Capability == MissingCapabilities.CompositeRecordLabel && u.ConceptId == c.Id);
            if (!recorded)
                e.Missing($"concept '{c.Name}' has no recordLabel — nothing decides how one of its records is "
                    + "named on screen. If it genuinely needs more than one field, record an unsupported item with "
                    + "capability 'composite_record_label' naming this concept");
            return;
        }
        var f = bp.Field(c.RecordLabel.FieldId);
        // A concept names the field that titles its records BEFORE the data pass creates it — the
        // concept pass is where "a ticket is known by its subject" is decided. So an unresolved
        // record label is a promise the data pass still owes, not a mistake.
        if (f is null)
            e.Missing($"concept '{c.Name}' has recordLabel field '{c.RecordLabel.FieldId}', which does not exist");
        else if (f.ConceptId != c.Id)
            e.Wrong($"concept '{c.Name}' uses '{f.Label}' as its record label, but that field belongs to another concept");
        else if (f.Computed is not null)
            e.Wrong($"concept '{c.Name}' uses the computed field '{f.Label}' as its record label — a derived value cannot name a record");
        else if (!fields.Any(x => x.Id == f.Id))
            e.Wrong($"concept '{c.Name}' record label '{f.Label}' is not among its fields");
    }

    /// <summary>The entity-vs-enumeration call, argued rather than guessed. Banning concepts named
    /// Status/Category/Type would be directionally right and categorically wrong — a category with
    /// its own owner, lifecycle or reporting role genuinely is an entity. So the rule triggers on
    /// SHAPE (this looks like a bare label list) and demands a stated reason, which is reviewable.</summary>
    private static void EntityVersusEnumeration(Blueprint bp, ConceptSpec c, GateErrors e)
    {
        if (c.Kind is not (ConceptKinds.Local or ConceptKinds.Config)) return;
        if (c.EntityJustification is not null) return;

        var fields = bp.AllFields.Where(f => f.ConceptId == c.Id).ToList();
        var substantive = fields.Count(f => f.Purpose != FieldPurposes.Identify);
        var hasWorkflow = bp.Workflows.Any(w => w.ConceptId == c.Id);
        var hasChildren = bp.Relationships.Any(r => r.IsComposition && r.FromConceptId == c.Id);
        var hasOwnReferences = bp.References.Any(r => r.OnConceptId == c.Id);

        // A gap, not a contradiction: mid-interview EVERY entity looks like a bare label set, because
        // the fields, workflows and links that would justify it have not been proposed yet.
        if (substantive == 0 && !hasWorkflow && !hasChildren && !hasOwnReferences)
            e.Missing($"concept '{c.Name}' is an entity but looks like a plain label set "
                + "(no fields beyond identification, no lifecycle, no children, no references of its own). "
                + "Either make it an enumeration, or declare entityJustification saying what earns it its own entity "
                + $"(one of: {string.Join(", ", new[] { "additional_properties", "own_lifecycle", "permissions", "hierarchy", "cross_record_reuse", "external_identity", "reporting_importance" })})");
    }

    // ---- externals --------------------------------------------------------------------------

    private static void Externals(Blueprint bp, GateErrors e)
    {
        foreach (var x in bp.Externals)
        {
            switch (x.Source)
            {
                case ExternalSources.Platform:
                    if (x.CoreSystemKey is not null)
                        e.Wrong($"external '{x.Name}' is a platform reference but names a coreSystemKey");
                    else if (!Schemas.PlatformEntityKeys.Contains(x.EntityKey))
                        e.Wrong($"external '{x.Name}' references unknown platform entity '{x.EntityKey}' "
                            + $"(known: {string.Join(", ", Schemas.PlatformEntityKeys.OrderBy(k => k))})");
                    break;

                case ExternalSources.Core:
                    if (x.CoreSystemKey is null)
                        e.Wrong($"external '{x.Name}' is a core reference but names no coreSystemKey — "
                            + "core apps are addressed by their permanent SystemKey, never by a handle");
                    else if (CoreAppRegistry.Find(x.CoreSystemKey) is not { } core)
                        e.Wrong($"external '{x.Name}' references unknown core app '{x.CoreSystemKey}'");
                    else if (!core.EntityKeys.Contains(x.EntityKey))
                        e.Wrong($"external '{x.Name}' references unknown entity '{x.EntityKey}' in core app "
                            + $"'{x.CoreSystemKey}' (known: {string.Join(", ", core.EntityKeys.OrderBy(k => k))})");
                    break;
            }
        }
    }

    // ---- relationships and references --------------------------------------------------------

    private static void Relationships(Blueprint bp, GateErrors e)
    {
        foreach (var r in bp.Relationships)
        {
            var from = bp.Concept(r.FromConceptId);
            var to = bp.Concept(r.ToConceptId);
            if (from is null) { e.Missing($"relationship '{r.Label}' comes from '{r.FromConceptId}', which is not a concept"); continue; }
            if (to is null) { e.Missing($"relationship '{r.Label}' points at '{r.ToConceptId}', which is not a concept"); continue; }

            if (!ConceptKinds.ProducesEntity(from.Kind))
                e.Wrong($"relationship '{r.Label}' comes from '{from.Name}', which is a {from.Kind} and has no records to relate");
            if (!ConceptKinds.ProducesEntity(to.Kind))
                e.Wrong($"relationship '{r.Label}' points at '{to.Name}', which is a {to.Kind} and has no records to relate");

            if (r.IsComposition)
            {
                if (r.Cardinality == Cardinalities.ManyToMany)
                    e.Wrong($"relationship '{r.Label}' is a composition but many-to-many — a record that belongs to several parents does not belong to any of them");
                if (r.StorageOwner != StorageOwners.To)
                    e.Wrong($"relationship '{r.Label}' is a composition, so the child must hold the reference (storageOwner 'to')");
                if (to.Kind != ConceptKinds.Subordinate)
                    e.Wrong($"relationship '{r.Label}' composes '{to.Name}', which is '{to.Kind}' — a composed child is 'subordinate'");
                if (r.DeleteBehavior != DeleteBehaviors.Cascade)
                    e.Wrong($"composition '{r.Label}' has deleteBehavior '{r.DeleteBehavior}' — a child that outlives its parent is orphaned data");
            }

            if (r.FromConceptId == r.ToConceptId && r.IsComposition)
                e.Wrong($"relationship '{r.Label}' composes '{from.Name}' into itself");

            if (r.Cardinality == Cardinalities.ManyToMany && r.Required)
                e.Wrong($"relationship '{r.Label}' is many-to-many and required — there is no single value to require");
        }
    }

    private static void References(Blueprint bp, GateErrors e)
    {
        foreach (var r in bp.References)
        {
            var on = bp.Concept(r.OnConceptId);
            if (on is null)
                e.Missing($"reference '{r.Label}' sits on '{r.OnConceptId}', which is not a concept");
            else if (!ConceptKinds.ProducesEntity(on.Kind))
                e.Wrong($"reference '{r.Label}' sits on '{on.Name}', which is a {on.Kind} and stores nothing");

            if (bp.Externals.All(x => x.Id != r.ExternalConceptId))
                e.Missing($"reference '{r.Label}' points at external '{r.ExternalConceptId}', which is not declared");
        }

        foreach (var g in bp.References.GroupBy(r => (r.OnConceptId, r.Key))
                     .Where(g => g.Count() > 1))
            e.Wrong($"concept '{bp.Concept(g.Key.OnConceptId)?.Name ?? g.Key.OnConceptId}' has two references keyed '{g.Key.Key}'");

        // A reference field and a data field on the same record cannot share a key — they become
        // the same column.
        foreach (var r in bp.References)
            if (bp.AllFields.Any(f => f.ConceptId == r.OnConceptId && f.Key == r.Key))
                e.Wrong($"'{r.Key}' is both a field and a reference on the same concept");
    }

    // ---- fields -----------------------------------------------------------------------------

    private static void Fields(Blueprint bp, GateErrors e)
    {
        foreach (var f in bp.AllFields)
        {
            var c = bp.Concept(f.ConceptId);
            if (c is null) { e.Missing($"field '{f.Label}' belongs to '{f.ConceptId}', which is not a concept"); continue; }

            if (FieldTypes.IsChoice(f.Type))
            {
                if (f.Options.Count == 0 && f.OptionsFromConceptId is null && !GovernedByWorkflow(bp, f))
                    e.Missing($"field '{f.Label}' is a {f.Type} with no options — list them, point at an enumeration concept, or let a workflow govern it");
                if (f.Options.Count > 0 && f.OptionsFromConceptId is not null)
                    e.Wrong($"field '{f.Label}' declares options AND points at an enumeration — pick one source");
                if (f.OptionsFromConceptId is { } src)
                {
                    var enumConcept = bp.Concept(src);
                    if (enumConcept is null)
                        e.Missing($"field '{f.Label}' takes options from '{src}', which is not a concept");
                    else if (enumConcept.Kind != ConceptKinds.Enumeration)
                        e.Wrong($"field '{f.Label}' takes options from '{enumConcept.Name}', which is a {enumConcept.Kind}, not an enumeration");
                }
            }
            else if (f.Options.Count > 0 || f.OptionsFromConceptId is not null)
            {
                e.Wrong($"field '{f.Label}' is a {f.Type} but carries options — only select and multiselect do");
            }

            if (f.Currency is not null && f.Type != FieldTypes.Money)
                e.Wrong($"field '{f.Label}' declares a currency but is a {f.Type}");
            if (f.Unit is not null && f.Type == FieldTypes.Money)
                e.Wrong($"field '{f.Label}' is money and declares a unit — money declares its unit with currency");

            Computed(bp, f, c, e);
        }

        foreach (var g in bp.AllFields.GroupBy(f => (f.ConceptId, f.Key)).Where(g => g.Count() > 1))
            e.Wrong($"concept '{bp.Concept(g.Key.ConceptId)?.Name ?? g.Key.ConceptId}' has two fields keyed '{g.Key.Key}'");

        foreach (var concept in bp.Concepts)
            ComputedCycles(bp, concept, e);
    }

    private static bool GovernedByWorkflow(Blueprint bp, FieldSpec f) =>
        bp.Workflows.Any(w => w.ConceptId == f.ConceptId && w.StatusFieldKey == f.Key);

    private static void Computed(Blueprint bp, FieldSpec f, ConceptSpec c, GateErrors e)
    {
        if (f.Computed is not { } comp) return;

        // Exactly one of expr/rollup. Enforced here rather than as a schema oneOf, which crashes the
        // JsonSchema.Net host the whole test suite runs on.
        var has = (comp.Expr is not null ? 1 : 0) + (comp.Rollup is not null ? 1 : 0);
        if (has == 0) { e.Missing($"field '{f.Label}' is computed but declares neither expr nor rollup"); return; }
        if (has == 2) { e.Wrong($"field '{f.Label}' declares both expr and rollup — a computed field is one or the other"); return; }

        if (!FieldTypes.IsNumeric(f.Type) && f.Type != FieldTypes.Boolean)
            e.Wrong($"field '{f.Label}' is computed but is a {f.Type} — computed values are numeric or boolean");
        if (f.Required)
            e.Wrong($"field '{f.Label}' is computed and required — the server writes it, so nobody can fail to supply it");

        if (comp.Expr is { } expr)
        {
            var validation = ComputedExpr.Validate(expr, id =>
            {
                var target = bp.Field(id);
                return target is null ? null
                    : FieldTypes.IsNumeric(target.Type) ? ComputedValueKind.Number
                    : target.Type == FieldTypes.Boolean ? ComputedValueKind.Boolean
                    : target.Type is FieldTypes.Date or FieldTypes.DateTime ? ComputedValueKind.Date
                    : null;
            }, id =>
            {
                var target = bp.Field(id);
                if (target is null)
                    return $"'{id}' is not a field";
                if (target.Id == f.Id)
                    return $"'{target.Label}' is the computed field itself";
                if (target.ConceptId != c.Id)
                    return $"'{target.Label}' belongs to another concept — an expression reads only its own record";
                if (!FieldTypes.IsNumeric(target.Type) && target.Type != FieldTypes.Boolean
                    && target.Type is not (FieldTypes.Date or FieldTypes.DateTime))
                    return $"'{target.Label}' is a {target.Type}, not a numeric, boolean, or date field";
                return null;
            }, dateId =>
            {
                var target = bp.Field(dateId);
                if (target is null) return $"'{dateId}' is not a field";
                if (target.ConceptId != c.Id) return $"'{target.Label}' belongs to another concept";
                if (target.Type is not (FieldTypes.Date or FieldTypes.DateTime))
                    return $"'{target.Label}' must be a date/datetime field (is '{target.Type}')";
                return null;
            });

            if (validation.Error is { } error)
                e.Wrong($"field '{f.Label}' expression — {error}");
            else
            {
                var expected = f.Type == FieldTypes.Boolean ? ComputedValueKind.Boolean : ComputedValueKind.Number;
                if (validation.ResultKind != expected)
                    e.Wrong($"field '{f.Label}' expression returns a {ComputedKindName(validation.ResultKind)}, not a {ComputedKindName(expected)}");
            }
        }

        if (comp.Rollup is { } roll)
        {
            if (!FieldTypes.IsNumeric(f.Type))
                e.Wrong($"field '{f.Label}' is a rollup but is a {f.Type} — rollups are numeric");
            var over = bp.Concept(roll.ConceptId);
            var via = bp.Relationship(roll.ViaRelationshipId);
            if (over is null) { e.Missing($"field '{f.Label}' rolls up '{roll.ConceptId}', which is not a concept"); return; }
            if (via is null) { e.Missing($"field '{f.Label}' rolls up via '{roll.ViaRelationshipId}', which is not a relationship"); return; }

            if (via.OwningConceptId != over.Id || via.TargetConceptId != c.Id)
                e.Wrong($"field '{f.Label}' rolls up '{over.Name}' via '{via.Label}', but that relationship does not point '{over.Name}' at '{c.Name}'");

            if (AggregateOps.NeedsField(roll.Op))
            {
                if (roll.FieldId is null)
                    e.Missing($"field '{f.Label}' rolls up with '{roll.Op}' but names no field to aggregate");
                else if (bp.Field(roll.FieldId) is not { } agg)
                    e.Missing($"field '{f.Label}' aggregates '{roll.FieldId}', which is not a field");
                else if (agg.ConceptId != over.Id)
                    e.Wrong($"field '{f.Label}' aggregates '{agg.Label}', which does not belong to '{over.Name}'");
                else if (!FieldTypes.IsNumeric(agg.Type))
                    e.Wrong($"field '{f.Label}' aggregates '{agg.Label}', which is a {agg.Type}");
            }
            else if (roll.FieldId is not null)
            {
                e.Wrong($"field '{f.Label}' counts records but also names a field to aggregate");
            }

            Filters(bp, roll.Filters, $"rollup on '{f.Label}'", over.Id, e);
        }
    }

    private static void ComputedCycles(Blueprint bp, ConceptSpec concept, GateErrors e)
    {
        var expressions = bp.AllFields
            .Where(field => field.ConceptId == concept.Id && field.Computed?.Expr is not null)
            .ToDictionary(field => field.Id, field => field.Computed!.Expr!, StringComparer.Ordinal);
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        var stack = new List<string>();
        var reported = new HashSet<string>(StringComparer.Ordinal);

        void Visit(string id)
        {
            if (state.GetValueOrDefault(id) == 2) return;
            if (state.GetValueOrDefault(id) == 1)
            {
                var start = stack.IndexOf(id);
                var cycle = stack.Skip(Math.Max(0, start)).Append(id).ToArray();
                var signature = string.Join("->", cycle);
                if (reported.Add(signature))
                    e.Wrong($"concept '{concept.Name}' has a computed expression cycle: "
                        + string.Join(" -> ", cycle.Select(fieldId => bp.Field(fieldId)?.Label ?? fieldId)));
                return;
            }

            state[id] = 1;
            stack.Add(id);
            foreach (var dependency in ComputedExpr.Identifiers(expressions[id]))
                if (expressions.ContainsKey(dependency)) Visit(dependency);
            stack.RemoveAt(stack.Count - 1);
            state[id] = 2;
        }

        foreach (var id in expressions.Keys) Visit(id);
    }

    private static string ComputedKindName(ComputedValueKind? kind) => kind switch
    {
        ComputedValueKind.Boolean => "boolean",
        ComputedValueKind.Date => "date",
        _ => "number",
    };

    /// <summary>Field ids referenced by a computed expression. Expressions are written with ids, not
    /// keys, so renaming a field can never silently break a formula.</summary>
    internal static IEnumerable<string> ExprFieldIds(string expr)
    {
        var i = 0;
        while (i < expr.Length)
        {
            if (expr[i] == 'f' && i + 4 <= expr.Length && expr.AsSpan(i).StartsWith("fld_"))
            {
                var j = i;
                while (j < expr.Length && (char.IsLetterOrDigit(expr[j]) || expr[j] == '_')) j++;
                yield return expr[i..j];
                i = j;
            }
            else i++;
        }
    }

    // ---- workflows --------------------------------------------------------------------------

    private static void Workflows(Blueprint bp, GateErrors e)
    {
        foreach (var w in bp.Workflows)
        {
            var c = bp.Concept(w.ConceptId);
            if (c is null) { e.Missing($"workflow '{w.Label}' governs '{w.ConceptId}', which is not a concept"); continue; }
            if (!ConceptKinds.ProducesEntity(c.Kind))
                e.Wrong($"workflow '{w.Label}' governs '{c.Name}', which is a {c.Kind} and has no records to move");

            var initial = w.States.Where(s => s.Initial).ToList();
            if (initial.Count == 0)
                e.Missing($"workflow '{w.Label}' has no initial state — a new record would start nowhere");
            else if (initial.Count > 1)
                e.Wrong($"workflow '{w.Label}' has {initial.Count} initial states ({string.Join(", ", initial.Select(s => s.Label))}) — a record starts in exactly one");

            if (!w.States.Any(s => s.Terminal))
                e.Missing($"workflow '{w.Label}' has no terminal state — nothing in it ever finishes");

            Duplicates(w.States.Select(s => s.Key),
                k => e.Wrong($"workflow '{w.Label}' has two states keyed '{k}'"));

            foreach (var t in w.Transitions)
            {
                var from = w.State(t.FromStateId);
                var to = w.State(t.ToStateId);
                if (from is null) { e.Missing($"transition '{t.Label}' starts at '{t.FromStateId}', which is not a state of '{w.Label}'"); continue; }
                if (to is null) { e.Missing($"transition '{t.Label}' ends at '{t.ToStateId}', which is not a state of '{w.Label}'"); continue; }
                if (from.Id == to.Id)
                    e.Wrong($"transition '{t.Label}' goes from '{from.Label}' to itself");
                if (from.Terminal)
                    e.Wrong($"transition '{t.Label}' leaves '{from.Label}', which is terminal");

                if (t.ActionId is { } aid && w.Actions.All(a => a.Id != aid))
                    e.Missing($"transition '{t.Label}' invokes action '{aid}', which '{w.Label}' does not declare");

                foreach (var fid in t.RequiresFieldIds)
                {
                    // The workflow pass runs before the data pass, so a required field is routinely a
                    // forward reference to something the data proposer still owes.
                    if (!bp.ResolveFieldTarget(fid).Found)
                        e.Missing($"transition '{t.Label}' requires '{fid}', which is not a field, reference or relationship");
                    else if (bp.OwnerOfFieldTarget(fid) is { } owner && owner != w.ConceptId)
                        e.Wrong($"transition '{t.Label}' requires '{fid}', which belongs to another concept");
                }

                if (t.Guard is { } guard)
                    Filters(bp, [guard], $"guard on transition '{t.Label}'", w.ConceptId, e);
            }

            // Every state must be reachable from the initial one, or records can never get there.
            if (initial.Count == 1) Unreachable(w, initial[0], e);

            foreach (var a in w.Actions)
                foreach (var fid in a.InputFieldIds)
                {
                    if (!bp.ResolveFieldTarget(fid).Found)
                        e.Missing($"action '{a.Label}' collects '{fid}', which is not a field, reference or relationship");
                    else if (bp.OwnerOfFieldTarget(fid) is { } owner && owner != w.ConceptId)
                        e.Wrong($"action '{a.Label}' collects '{fid}', which belongs to another concept");
                }

            // The status field is created by lowering from the workflow itself. A data field with the
            // same key would collide, and its options would fight the process for ownership.
            if (bp.AllFields.FirstOrDefault(f => f.ConceptId == w.ConceptId && f.Key == w.StatusFieldKey) is { } status)
            {
                if (status.Options.Count > 0 || status.OptionsFromConceptId is not null)
                    e.Wrong($"field '{status.Label}' is governed by workflow '{w.Label}' and must not declare its own options — the workflow's states are its options");
                if (!FieldTypes.IsChoice(status.Type))
                    e.Wrong($"field '{status.Label}' is governed by workflow '{w.Label}' but is a {status.Type} — a governed status field is a select");
            }
        }

        foreach (var g in bp.Workflows.GroupBy(w => w.ConceptId).Where(g => g.Count() > 1))
            e.Wrong($"concept '{bp.Concept(g.Key)?.Name ?? g.Key}' has {g.Count()} workflows — one lifecycle per concept");
    }

    private static void Unreachable(WorkflowSpec w, StateSpec initial, GateErrors e)
    {
        var reached = new HashSet<string> { initial.Id };
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var t in w.Transitions)
                if (reached.Contains(t.FromStateId) && reached.Add(t.ToStateId)) grew = true;
        }
        foreach (var s in w.States.Where(s => !reached.Contains(s.Id)))
            e.Missing($"state '{s.Label}' of workflow '{w.Label}' cannot be reached from '{initial.Label}' — no sequence of transitions leads there");
    }

    // ---- jobs -------------------------------------------------------------------------------

    private static void Jobs(Blueprint bp, GateErrors e)
    {
        foreach (var j in bp.Jobs)
        {
            if (bp.Actor(j.ActorId) is null)
                e.Missing($"job '{j.Task}' belongs to actor '{j.ActorId}', which is not declared");
            if (bp.Concept(j.PrimaryConceptId) is null)
                e.Missing($"job '{j.Task}' is about '{j.PrimaryConceptId}', which is not a concept");
            if (j.ServedBySurfaceId is { } sid && bp.Experience?.Surface(sid) is null)
                e.Missing($"job '{j.Task}' is served by surface '{sid}', which does not exist");
        }
    }

    // ---- experience -------------------------------------------------------------------------

    private static void Experience(Blueprint bp, GateErrors e)
    {
        if (bp.Experience is not { } x) return;

        var homes = x.Pages.Count(p => p.Role == PageRoles.Home);
        if (homes > 1) e.Wrong($"{homes} pages claim the 'home' role — one app has one landing page");

        foreach (var p in x.Pages)
            foreach (var sid in p.SurfaceIds)
                if (x.Surface(sid) is null)
                    e.Missing($"page '{p.Label}' hosts surface '{sid}', which does not exist");

        foreach (var l in x.Landing)
        {
            if (bp.Actor(l.ActorId) is null)
                e.Missing($"a landing rule names actor '{l.ActorId}', which is not declared");
            if (x.Page(l.PageId) is null)
                e.Missing($"a landing rule sends an actor to page '{l.PageId}', which does not exist");
        }

        var placed = x.Pages.SelectMany(p => p.SurfaceIds).ToHashSet(StringComparer.Ordinal);
        foreach (var s in x.Surfaces)
        {
            var c = bp.Concept(s.ConceptId);
            if (c is null) { e.Missing($"surface '{s.Label}' is over '{s.ConceptId}', which is not a concept"); continue; }
            if (!ConceptKinds.ProducesEntity(c.Kind))
                e.Wrong($"surface '{s.Label}' is over '{c.Name}', which is a {c.Kind} and has no records to show");

            if (s.Scope == SurfaceScopes.PerRecord)
            {
                if (s.ScopeConceptId is null)
                    e.Missing($"surface '{s.Label}' is per-record but names no scope concept — one of WHAT?");
                else if (bp.Concept(s.ScopeConceptId) is null)
                    e.Missing($"surface '{s.Label}' is scoped per '{s.ScopeConceptId}', which is not a concept");
            }
            else if (!placed.Contains(s.Id) && s.Surface != SurfaceKinds.Detail)
            {
                e.Missing($"surface '{s.Label}' is global but no page hosts it — it would exist and be unreachable");
            }

            foreach (var col in s.Columns)
                if (!bp.ResolveFieldTarget(col).Found)
                    e.Missing($"surface '{s.Label}' shows column '{col}', which is not a field or reference");

            foreach (var so in s.Sort)
                if (!bp.ResolveFieldTarget(so.FieldId).Found)
                    e.Missing($"surface '{s.Label}' sorts by '{so.FieldId}', which is not a field or reference");

            foreach (var fid in s.SortableFieldIds)
                if (!bp.ResolveFieldTarget(fid).Found)
                    e.Missing($"surface '{s.Label}' offers '{fid}' for sorting, but it is not a field or reference");

            foreach (var fid in s.FilterableFieldIds)
            {
                var (found, type) = bp.ResolveFieldTarget(fid);
                if (!found)
                {
                    e.Missing($"surface '{s.Label}' offers '{fid}' for filtering, but it is not a field or reference");
                }
                else if (!Filterable(type))
                {
                    // The filter bar is facets over closed sets plus a text search. A money or date
                    // range reads like a filter to a user but has nowhere to go, so saying so here
                    // beats approving a control that cannot be built.
                    e.Wrong($"surface '{s.Label}' offers '{fid}' (a {type}) for filtering. "
                        + "A filter bar is made of closed-set facets (select, multiselect, reference) "
                        + "and a text search — a " + type + " is neither");
                }
            }

            Filters(bp, s.Filters, $"surface '{s.Label}'", s.ConceptId, e);
            foreach (var m in s.Metrics) Metric(bp, s, m, e);

            SurfaceDataContract(bp, s, c, e);
        }
    }

    /// <summary>
    /// The surface → data contracts. A surface the user approved has to be BUILDABLE from the data
    /// they approved alongside it, and these are the pairings where that can silently fail: a board
    /// with nothing to group by, a calendar with no dates, a dashboard whose numbers have no source.
    ///
    /// <para>Every one of these was previously discovered after generation, as a screen that looked
    /// broken. Discovering it at approval time turns it into one more question in a conversation the
    /// user is already having.</para>
    /// </summary>
    private static void SurfaceDataContract(Blueprint bp, SurfaceSpec s, ConceptSpec c, GateErrors e)
    {
        switch (s.Surface)
        {
            case SurfaceKinds.Board:
                if (s.GroupByFieldId is null)
                {
                    e.Missing($"board '{s.Label}' has no groupByFieldId — a board is columns, and nothing says what the columns are");
                    break;
                }
                var g = bp.Field(s.GroupByFieldId);
                if (g is null)
                    e.Missing($"board '{s.Label}' groups by '{s.GroupByFieldId}', which is not a field");
                else if (g.ConceptId != c.Id)
                    e.Wrong($"board '{s.Label}' groups by '{g.Label}', which belongs to another concept");
                else if (!FieldTypes.IsChoice(g.Type))
                    e.Wrong($"board '{s.Label}' groups by '{g.Label}', which is a {g.Type} — columns come from a closed set, so this needs a select or a workflow-governed status");
                else if (g.Options.Count == 0 && g.OptionsFromConceptId is null && !GovernedByWorkflow(bp, g))
                    e.Missing($"board '{s.Label}' groups by '{g.Label}', which has no values — there would be no columns");
                break;

            case SurfaceKinds.Calendar:
                if (s.DateStartFieldId is null)
                {
                    e.Missing($"calendar '{s.Label}' has no dateStartFieldId — nothing places a record in time");
                    break;
                }
                foreach (var (fid, which) in new[] { (s.DateStartFieldId, "start"), (s.DateEndFieldId, "end") })
                {
                    if (fid is null) continue;
                    var f = bp.Field(fid);
                    if (f is null)
                        e.Missing($"calendar '{s.Label}' takes its {which} from '{fid}', which is not a field");
                    else if (f.ConceptId != c.Id)
                        e.Wrong($"calendar '{s.Label}' takes its {which} from '{f.Label}', which belongs to another concept");
                    else if (!FieldTypes.IsTemporal(f.Type))
                        e.Wrong($"calendar '{s.Label}' takes its {which} from '{f.Label}', which is a {f.Type}");
                }
                break;

            case SurfaceKinds.Dashboard:
                if (s.Metrics.Count == 0)
                    e.Missing($"dashboard '{s.Label}' defines no metrics — reading the numbers is the job, and there are none");
                break;

            case SurfaceKinds.Table:
                if (s.Columns.Count == 0)
                    e.Missing($"table '{s.Label}' has no columns — a table of nothing is a list of blank rows");
                if (s.SortableFieldIds.Count == 0 && s.Sort.Count == 0)
                    e.Missing($"table '{s.Label}' has nothing sortable — comparing records is what a table is for");
                if (s.FilterableFieldIds.Count == 0)
                    e.Missing($"table '{s.Label}' has nothing filterable — finding a record means scrolling");
                break;

            case SurfaceKinds.Detail:
                // A recorded composite-label gap is not the same as forgetting one — RecordLabelRules
                // already reported the former, and reporting it twice from here would be the cascade
                // this gate is built to avoid.
                if (c.RecordLabel is null && c.Kind != ConceptKinds.Settings
                    && !bp.Unsupported.Any(u => u.Capability == MissingCapabilities.CompositeRecordLabel
                                                && u.ConceptId == c.Id))
                    e.Missing($"detail '{s.Label}' shows a '{c.Name}' record, which has no record label to head it with");
                foreach (var rid in s.RelatedConceptIds)
                    if (bp.Concept(rid) is null)
                        e.Missing($"detail '{s.Label}' shows related concept '{rid}', which does not exist");
                break;
        }
    }

    /// <summary>What a filter bar can actually be built from: closed-set facets, or a text search.</summary>
    internal static bool Filterable(string? type) =>
        type is FieldTypes.Select or FieldTypes.MultiSelect or FieldTypes.Reference or FieldTypes.Text;

    /// <summary>A facet is a chip of known values; free text becomes the search box instead.</summary>
    internal static bool IsFacet(string? type) =>
        type is FieldTypes.Select or FieldTypes.MultiSelect or FieldTypes.Reference;

    private static void Metric(Blueprint bp, SurfaceSpec s, MetricSpec m, GateErrors e)
    {
        var over = bp.Concept(m.ConceptId);
        if (over is null) { e.Missing($"metric '{m.Label}' on '{s.Label}' counts '{m.ConceptId}', which is not a concept"); return; }

        if (AggregateOps.NeedsField(m.Aggregate))
        {
            if (m.FieldId is null)
                e.Missing($"metric '{m.Label}' uses '{m.Aggregate}' but names no field");
            else if (bp.Field(m.FieldId) is not { } f)
                e.Missing($"metric '{m.Label}' aggregates '{m.FieldId}', which is not a field");
            else if (f.ConceptId != over.Id)
                e.Wrong($"metric '{m.Label}' aggregates '{f.Label}', which does not belong to '{over.Name}'");
            else if (!FieldTypes.IsNumeric(f.Type))
                e.Wrong($"metric '{m.Label}' aggregates '{f.Label}', which is a {f.Type}");
        }

        if (m.GroupByFieldId is { } gid && !bp.ResolveFieldTarget(gid).Found)
            e.Missing($"metric '{m.Label}' groups by '{gid}', which is not a field or reference");

        Filters(bp, m.Filters, $"metric '{m.Label}'", over.Id, e);
    }

    // ---- filters ----------------------------------------------------------------------------

    private static void Filters(Blueprint bp, IReadOnlyList<Filter> filters, string where,
        string conceptId, GateErrors e)
    {
        foreach (var f in filters)
        {
            var (found, type) = bp.ResolveFieldTarget(f.FieldId);
            if (!found)
            {
                e.Missing($"{where} filters on '{f.FieldId}', which is not a field or reference");
                continue;
            }

            var owner = bp.OwnerOfFieldTarget(f.FieldId);
            if (owner is not null && owner != conceptId)
                e.Wrong($"{where} filters on a field of another concept");

            var valueless = FilterOperators.IsValueless(f.Operator);
            if (valueless)
            {
                if (f.Value is not null)
                    e.Wrong($"{where} uses '{f.Operator}', which takes no value, but supplies one");
                continue;
            }

            if (f.Value is not { } v)
            {
                e.Missing($"{where} filters with '{f.Operator}' but supplies no value");
                continue;
            }

            // Exactly one of literal/context — a semantic rule because a schema oneOf here would
            // crash the JsonSchema.Net host.
            var has = (v.Literal is not null ? 1 : 0) + (v.Context is not null ? 1 : 0);
            if (has == 0)
                e.Missing($"{where} supplies a filter value that is neither a literal nor a context");
            else if (has == 2)
                e.Wrong($"{where} supplies both a literal and a context for one filter value");
            else if (v.Context is { } ctx)
            {
                if (ctx == FilterContexts.CurrentActor && type is not (FieldTypes.Reference or FieldTypes.Text))
                    e.Wrong($"{where} compares '{f.FieldId}' (a {type}) to the current actor — that needs a reference to a person");
                if (ctx == FilterContexts.Today && type is not null && !FieldTypes.IsTemporal(type))
                    e.Wrong($"{where} compares '{f.FieldId}' (a {type}) to '{ctx}', which is a date");
            }
        }
    }

    // ---- scenarios --------------------------------------------------------------------------

    private static void Scenarios(Blueprint bp, GateErrors e)
    {
        foreach (var s in bp.Scenarios)
        {
            if (bp.Actor(s.ActorId) is null)
                e.Missing($"scenario '{s.Title}' runs as actor '{s.ActorId}', which is not declared");

            var bound = new HashSet<string>(StringComparer.Ordinal);
            foreach (var step in s.Given.Concat(s.Steps))
            {
                if (step.AsActorId is { } stepActor && bp.Actor(stepActor) is null)
                    e.Missing($"scenario '{s.Title}' has a step run as actor '{stepActor}', which is not declared");
                StepErrors(bp, s, step, bound, e);
                if (step.As is { } name && !bound.Add(name))
                    e.Wrong($"scenario '{s.Title}' binds the name '{name}' twice");
            }

            foreach (var x in s.Expect)
            {
                if (x.Record is { } r && !bound.Contains(r.Ref))
                    e.Missing($"scenario '{s.Title}' expects something of '{r.Ref}', which no step bound");
                if (x.Op is ScenarioExpectOps.FieldEquals && x.FieldId is null)
                    e.Missing($"scenario '{s.Title}' expects a field to equal something but names no field");
                if (x.Op is ScenarioExpectOps.VisibleIn or ScenarioExpectOps.NotVisibleIn)
                {
                    if (x.SurfaceId is null)
                        e.Missing($"scenario '{s.Title}' expects visibility but names no surface");
                    else if (bp.Experience?.Surface(x.SurfaceId) is null)
                        e.Missing($"scenario '{s.Title}' expects visibility in surface '{x.SurfaceId}', which does not exist");
                }
                if (x.AsActorId is { } aid && bp.Actor(aid) is null)
                    e.Missing($"scenario '{s.Title}' checks visibility as actor '{aid}', which is not declared");
            }

            foreach (var rid in s.VerifiesRequirementIds)
                if (bp.Ledger.All(r => r.Id != rid))
                    e.Missing($"scenario '{s.Title}' claims to verify '{rid}', which is not in the ledger");

            var refuses = s.Given.Concat(s.Steps).Any(st => st.ExpectRefused);
            if (s.Negative && !refuses)
                e.Wrong($"scenario '{s.Title}' is marked negative but no step expects to be refused — nothing about it proves a prohibition");
            if (!s.Negative && refuses)
                e.Wrong($"scenario '{s.Title}' expects a step to be refused but is not marked negative");
        }
    }

    private static void StepErrors(Blueprint bp, ScenarioSpec s, ScenarioStep step,
        HashSet<string> bound, GateErrors e)
    {
        if (step.ConceptId is { } cid && bp.Concept(cid) is null)
            e.Missing($"scenario '{s.Title}' acts on '{cid}', which is not a concept");
        if (step.AsActorId is { } actorId && bp.Actor(actorId) is null)
            e.Missing($"scenario '{s.Title}' has a step run as actor '{actorId}', which is not declared");

        if (step.Record is { } r && !bound.Contains(r.Ref))
            e.Missing($"scenario '{s.Title}' refers to '{r.Ref}' before any step bound it");

        switch (step.Op)
        {
            case ScenarioOps.Create when step.ConceptId is null:
                e.Missing($"scenario '{s.Title}' creates a record without saying of what");
                break;
            case ScenarioOps.Command when step.ActionId is null:
                e.Missing($"scenario '{s.Title}' runs a command without naming it");
                break;
            case ScenarioOps.Command when bp.Workflows.SelectMany(w => w.Actions).All(a => a.Id != step.ActionId):
                e.Missing($"scenario '{s.Title}' runs action '{step.ActionId}', which no workflow declares");
                break;
            case ScenarioOps.Transition when step.TransitionId is null:
                e.Missing($"scenario '{s.Title}' transitions without naming the transition");
                break;
            case ScenarioOps.Transition when bp.Workflows.SelectMany(w => w.Transitions).All(t => t.Id != step.TransitionId):
                e.Missing($"scenario '{s.Title}' uses transition '{step.TransitionId}', which no workflow declares");
                break;
            case ScenarioOps.Update or ScenarioOps.Delete when step.Record is null:
                e.Missing($"scenario '{s.Title}' has a '{step.Op}' step with no record to act on");
                break;
        }

        foreach (var key in step.Values?.Keys ?? Enumerable.Empty<string>())
            if (!bp.ResolveFieldTarget(key).Found)
                e.Missing($"scenario '{s.Title}' sets '{key}', which is not a field or reference");
    }

    // ---- ledger -----------------------------------------------------------------------------

    private static void Ledger(Blueprint bp, GateErrors e)
    {
        Duplicates(bp.Ledger.Select(r => r.Id),
            id => e.Wrong($"requirement '{id}' appears twice in the ledger"));

        var elementIds = AllElementIds(bp);

        foreach (var r in bp.Ledger)
        {
            foreach (var c in r.CoveredBy)
                if (!elementIds.Contains(c))
                    e.Missing($"requirement '{r.Id}' claims to be covered by '{c}', which is not an element of this blueprint");

            foreach (var v in r.VerifiedBy)
                if (bp.Scenarios.All(s => s.Id != v))
                    e.Missing($"requirement '{r.Id}' claims to be verified by scenario '{v}', which does not exist");

            // A prohibition nothing tests negatively is a prohibition the app will not enforce, and
            // every positive scenario will still pass.
            if (r.ImpliesProhibition && r.VerifiedBy.Count > 0
                && !r.VerifiedBy.Any(v => bp.Scenarios.FirstOrDefault(s => s.Id == v)?.Negative == true))
                e.Missing($"requirement '{r.Id}' says something must NOT be possible, but none of its scenarios is negative");

            if (r.DesignCoverage is { } dc && !DesignCoverages.All.Contains(dc))
                e.Wrong($"requirement '{r.Id}' has an unknown designCoverage '{dc}'");
        }

        foreach (var u in bp.Unsupported)
        {
            if (u.ConceptId is { } ucid && bp.Concept(ucid) is null)
                e.Missing($"unsupported item '{u.Id}' is about concept '{ucid}', which does not exist");

            if (u.RequirementId is { } rid)
            {
                var req = bp.Ledger.FirstOrDefault(r => r.Id == rid);
                if (req is null)
                    e.Missing($"unsupported item '{u.Id}' refers to requirement '{rid}', which is not in the ledger");
                else if (req.Importance == Importances.Critical && !u.BlocksPublication)
                    e.Wrong($"unsupported item '{u.Id}' blocks a CRITICAL requirement but is not marked blocksPublication — "
                        + "an app that quietly does not do what it was asked for must not publish");
            }
        }
    }

    /// <summary>Every id a requirement may legitimately name as covering it.</summary>
    private static HashSet<string> AllElementIds(Blueprint bp)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in bp.Concepts) ids.Add(c.Id);
        foreach (var f in bp.AllFields) ids.Add(f.Id);
        foreach (var r in bp.Relationships) ids.Add(r.Id);
        foreach (var r in bp.References) ids.Add(r.Id);
        foreach (var x in bp.Externals) ids.Add(x.Id);
        foreach (var a in bp.Actors) ids.Add(a.Id);
        foreach (var w in bp.Workflows)
        {
            ids.Add(w.Id);
            foreach (var s in w.States) ids.Add(s.Id);
            foreach (var t in w.Transitions) ids.Add(t.Id);
            foreach (var a in w.Actions) ids.Add(a.Id);
        }
        foreach (var s in bp.Experience?.Surfaces ?? []) ids.Add(s.Id);
        foreach (var p in bp.Experience?.Pages ?? []) ids.Add(p.Id);
        foreach (var m in (bp.Experience?.Surfaces ?? []).SelectMany(s => s.Metrics)) ids.Add(m.Id);
        foreach (var j in bp.Jobs) ids.Add(j.Id);
        return ids;
    }
}

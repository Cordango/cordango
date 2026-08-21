// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Definition;

namespace Cordango.Cord;

/// <summary>
/// Cord's own semantic checks, and nothing the Gate already does.
///
/// <para>The whole list is in <see cref="CordErrorCode"/> and it is deliberately eleven items long.
/// These are the questions that only exist because Cord has a vocabulary: an aggregate naming an
/// entity that is not there, a relationship that cannot be resolved to a join, an expression that does
/// not parse. Everything about whether the resulting APPLICATION is correct — types, cycles, filters,
/// reachable process states, resolvable roles — belongs to <c>Gate.Validate</c> on the lowered
/// document, and its wording reaches the model verbatim.</para>
///
/// <para>The one thing worth being strict about here is <b>joins</b>. A rollup wired through the wrong
/// reference is structurally valid, compiles cleanly, and reports a wrong number on a screen somebody
/// trusts. The gate cannot catch that because both joins are legal; Cord can, because it knows the
/// author never chose one.</para>
/// </summary>
public static class CordCheck
{
    public static IReadOnlyList<CordError> Errors(CordApp app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var errors = new List<CordError>();
        var refs = CordRefIndex.FromModel(app.EntityList);

        var seenEntities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entity in app.EntityList)
        {
            if (entity.Key is not { Length: > 0 } key)
            {
                errors.Add(new CordError(CordErrorCode.MalformedOperation, "entities",
                    "an entity has no key"));
                continue;
            }
            if (!seenEntities.Add(key))
                errors.Add(new CordError(CordErrorCode.DuplicateEntity, $"entities/{key}",
                    $"'{key}' is declared more than once"));

            var seenFields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in entity.FieldList)
            {
                if (field.Key is not { Length: > 0 } fk)
                {
                    errors.Add(new CordError(CordErrorCode.MalformedOperation, $"entities/{key}/fields",
                        "a field has no key"));
                    continue;
                }
                if (!seenFields.Add(fk))
                    errors.Add(new CordError(CordErrorCode.DuplicateField, $"entities/{key}/fields/{fk}",
                        $"'{fk}' is declared more than once on '{key}'"));

                CheckCalc(errors, refs, app, entity, key, field, fk);
            }
        }

        return errors;
    }

    private static void CheckCalc(List<CordError> errors, CordRefIndex refs, CordApp app,
        CordEntity entity, string entityKey, CordField field, string fieldKey)
    {
        var where = $"entities/{entityKey}/fields/{fieldKey}";

        switch (field.Calc)
        {
            case null:
                return;

            case CordExpr expr:
            {
                // Delegated ENTIRELY. ComputedExpr is 542 tested lines that already know prev(), pow(),
                // duration functions, typed comparison and the acyclicity rule. Cord owns no grammar
                // here and must never grow one — this call is the whole of Cord's expression support.
                var types = entity.FieldList
                    .Where(f => f.Key is not null)
                    .ToDictionary(f => f.Key!, f => f.Type, StringComparer.Ordinal);

                var result = ComputedExpr.Validate(expr.Expr,
                    fieldKind: name => TypeOf(name) is { } t ? Kind(t) : null,
                    // A dotted identifier is a hop onto a referenced record. Whether the hop is LEGAL
                    // stays the gate's question — it owns the one-hop rule and the cross-app rule — but
                    // the TYPE has to be answered here, because the parser cannot proceed without it.
                    identError: ident =>
                        ident.Contains('.') || types.ContainsKey(ident)
                            ? null
                            : $"'{ident}' is not a field on '{entityKey}'");

                if (result.Error is { } message)
                    errors.Add(new CordError(CordErrorCode.InvalidExpression, where, message));
                return;

                // <summary>
                // The type of an identifier, following ONE reference hop.
                //
                // <para>Without this, every expression that reads through a reference was refused
                // before the platform ever saw it — including `prev(cash_end, scenario.starting_cash)`,
                // which is how a running balance is written and which the corpus does, gate-clean, four
                // times in one app. A budget model was therefore unauthorable, and Cord was breaking
                // rule 1 in the worst direction: deciding something the gate already decides, and
                // deciding it wrong.
                //
                // <para>Only the resolution is mirrored, not the rules. Whether two hops are allowed,
                // whether a cross-app reference may be read, what the error should say — all the gate's,
                // which is why an unresolvable hop returns null here rather than an error. The parser
                // then says the identifier is not a numeric, boolean or date field, and the gate says
                // precisely why.
                // </summary>
                string? TypeOf(string ident)
                {
                    if (types.TryGetValue(ident, out var own)) return own;
                    if (ComputedExpr.Hop(ident) is not { } hop) return null;

                    // The hop's first half must be a reference field on THIS entity, pointing inside
                    // this app — a platform reference targets records Cord's model does not contain.
                    var reference = entity.FieldList.FirstOrDefault(f => f.Key == hop.Reference);
                    if (reference is not { Type: "reference", TargetApp: null, TargetEntity: { } target })
                        return null;

                    return app.EntityList
                        .FirstOrDefault(e => e.Key == target)?.FieldList
                        .FirstOrDefault(f => f.Key == hop.Field)?.Type;
                }
            }

            case CordAggregate agg:
            {
                if (!app.EntityList.Any(e => e.Key == agg.Of))
                {
                    errors.Add(new CordError(CordErrorCode.UnknownEntity, where,
                        $"there is no entity '{agg.Of}' to aggregate"));
                    return;
                }

                var wantsField = agg.Op != "count";
                if (wantsField && agg.Field is null)
                    errors.Add(new CordError(CordErrorCode.AggregateFieldMismatch, where,
                        $"'{agg.Op}' needs a field of '{agg.Of}' to add up"));
                if (!wantsField && agg.Field is not null)
                    errors.Add(new CordError(CordErrorCode.AggregateFieldMismatch, where,
                        "'count' counts records and takes no field"));

                // `over` is either "mine" (children pointing here) or a reference on THIS entity whose
                // target both records share.
                if (agg.Over != CordAggregate.Mine && refs.TargetOf(entityKey, agg.Over) is null)
                {
                    errors.Add(new CordError(CordErrorCode.UnknownRelationship, where,
                        $"'{agg.Over}' is not a reference field on '{entityKey}' — use \"mine\" for "
                        + $"records that point at this one, or name a reference they share"));
                    return;
                }

                // An explicitly supplied join is the author's business; only derive when they left it.
                if (agg.Via is not null) return;

                if (refs.Infer(entityKey, agg.Of, agg.Over) is not null) return;

                var candidates = refs.Candidates(entityKey, agg.Of, agg.Over);
                errors.Add(candidates.Count > 1
                    ? new CordError(CordErrorCode.AmbiguousJoin, where,
                        $"'{agg.Of}' has more than one reference that could join it here, so which "
                        + "records to add up is a guess", Candidates: candidates)
                    : new CordError(CordErrorCode.UnresolvableJoin, where,
                        $"nothing on '{agg.Of}' points at "
                        + (agg.Over == CordAggregate.Mine ? $"'{entityKey}'" : $"what '{agg.Over}' points at")
                        + " — add a reference field to it first"));
                return;
            }
        }
    }

    /// <summary>Field type → what an expression can do with it. The same three-way split
    /// <c>Gate</c> uses; an unrecognised type is null, which lets <c>ComputedExpr</c> report it in its
    /// own words rather than having Cord invent a message for it.</summary>
    private static ComputedValueKind? Kind(string? type) => type switch
    {
        "integer" or "decimal" or "money" => ComputedValueKind.Number,
        "boolean" => ComputedValueKind.Boolean,
        "date" or "datetime" => ComputedValueKind.Date,
        _ => null,
    };

    /// <summary>
    /// Every entity a screen names — its subject AND each section's — resolves.
    ///
    /// <para>Per SECTION, not per screen, because a screen may legitimately draw on several entities;
    /// checking only a screen-level entity is what the entity-keyed version of this op could do, and
    /// that shape is exactly what could not express a real overview page. Everything else about a page
    /// — block validity, view references, filter paths — is the gate's.</para>
    /// </summary>
    public static IReadOnlyList<CordError> ScreenErrors(CordApp app, IEnumerable<CordScreen> screens)
    {
        var known = app.EntityList.Where(e => e.Key is not null).Select(e => e.Key!)
            .ToHashSet(StringComparer.Ordinal);

        var errors = screens
            .SelectMany(s => s.EntityKeys.Select(k => (Screen: s.Key ?? s.Label ?? "?", Entity: k)))
            .Where(x => !known.Contains(x.Entity))
            .Select(x => new CordError(CordErrorCode.UnknownScreenEntity,
                $"screens/{x.Screen}",
                $"there is no entity '{x.Entity}' for this screen to show"))
            .ToList();

        // A SCREEN SHOWS SOMETHING. Sections, tabs, or both — the Budget Planner's Hiring Plan is a
        // headline row above four tabs, so these are not alternatives. A screen with neither lowers to a
        // page with no blocks: valid, navigable, and empty.
        foreach (var screen in screens)
        {
            if (screen.SectionList.Count == 0 && screen.TabList.Count == 0)
                errors.Add(new CordError(CordErrorCode.UnknownScreenEntity,
                    $"screens/{screen.Key ?? screen.Label ?? "?"}",
                    $"the screen '{screen.Label ?? screen.Key ?? "?"}' has no sections and no tabs, so "
                    + "there would be nothing on it"));

            // A duplicate tab key is two tabs with one identity: the second silently wins every later
            // upsert_screen_tab, so the tab somebody edits is not the tab they were looking at.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tab in screen.TabList)
                if (!seen.Add(tab.Key))
                    errors.Add(new CordError(CordErrorCode.DuplicateTab,
                        $"screens/{screen.Key ?? "?"}/tabs/{tab.Key}",
                        $"this screen already has a tab '{tab.Key}' — one key, one tab"));
            // Section identity is screen-local, including tabs and split children. It becomes the
            // lowered view key (`<screen>__<section>`), so a duplicate would either collide or force
            // the lowerer to rename an identity the author explicitly chose.
            var sectionKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var section in screen.AllSections.Concat(
                screen.AllSections.SelectMany(x => x.SectionList)))
            {
                if (section.Kind is not (CordSectionKinds.List or CordSectionKinds.Metric
                    or CordSectionKinds.Chart)) continue;

                // Co-creation v1.1 authors explicit keys. Null remains tolerated here so generic Cord
                // documents and journals written before that contract still replay; they lower through
                // the deterministic fallback and acquire explicit keys when raised or re-authored.
                if (section.Key is { Length: > 0 } key && !sectionKeys.Add(key))
                    errors.Add(new CordError(CordErrorCode.MalformedOperation,
                        $"screens/{screen.Key ?? "?"}/sections/{key}",
                        $"this screen already has a section '{key}' - one key, one section"));
            }
        }

        // A CALENDAR MUST SAY WHICH DATE. The renderer falls back to the entity's first date field, so
        // this is not something the gate will ever complain about — the document is valid and answers a
        // different question than its heading promises. See CordSection.DateField.
        // A split holds two pieces of CONTENT. The schema pins the count at two and cannot pin what they
        // are without a second copy of the whole section shape, which would put the UI schema through its
        // ceiling — so this one rule is a check, and the byte budget is the honest reason. Refused rather
        // than flattened: a split inside a split is a grid, which is what the two-up split was chosen
        // over. See CordSection.Sections.
        foreach (var screen in screens)
            // AllSections, not SectionList: a section inside a tab is a section on this screen, and a
            // check that stopped at the shared ones would let a tab carry the calendar or the nested
            // split the rule exists to refuse.
            foreach (var section in screen.AllSections.Concat(
                screen.AllSections.SelectMany(x => x.SectionList)))
            {
                if (section.Kind != CordSectionKinds.Split && section.Sections is { Count: > 0 })
                    errors.Add(new CordError(CordErrorCode.NestedSplit,
                        $"screens/{screen.Key ?? "?"}/sections",
                        $"a '{section.Kind}' section carries `sections`, which only a split may — "
                        + "either make it a split or move those sections up beside it"));

                if (section.Kind != CordSectionKinds.Split) continue;
                foreach (var side in section.SectionList)
                    if (side.Kind == CordSectionKinds.Split)
                        errors.Add(new CordError(CordErrorCode.NestedSplit,
                            $"screens/{screen.Key ?? "?"}/sections",
                            "a split inside a split is a grid — put the two things that are read "
                            + "together in one split, and let anything else stack below it"));
            }

        foreach (var screen in screens)
            // AllSections, not SectionList: a section inside a tab is a section on this screen, and a
            // check that stopped at the shared ones would let a tab carry the calendar or the nested
            // split the rule exists to refuse.
            foreach (var section in screen.AllSections.Concat(
                screen.AllSections.SelectMany(x => x.SectionList)))
                if (section.View == "calendar" && section.DateField is null)
                    errors.Add(new CordError(CordErrorCode.CalendarNeedsDateField,
                        $"screens/{screen.Key ?? screen.Label ?? "?"}/sections",
                        $"the calendar '{section.Label ?? section.Of ?? "?"}' does not say which date "
                        + "places a record on it — name the one this calendar is about in `dateField`"));

        return errors;
    }

    // ---- behaviour: only what is needed to interpret CORD ----------------------------------------

    /// <summary>
    /// A behaviour declaration naming an entity that does not exist.
    ///
    /// <para><b>Rule 1 is the whole design of this method.</b> <c>Gate.cs</c> already owns whether a
    /// transition's <c>to</c> is a declared state, whether a role grants a command that exists, whether
    /// an effect's <c>set</c> names real fields, and 350 other rules. None of that is restated here.
    /// What IS here is the one question the gate cannot answer usefully: an operation naming an unknown
    /// entity is a CORD-level mistake, and reporting it in Cord's terms and at the operation's index is
    /// the difference between "fix operation 2" and a JSON Pointer into a document the author never
    /// saw.</para>
    /// </summary>
    public static IReadOnlyList<CordError> BehaviourEntityErrors(
        CordApp app, IEnumerable<(string? Entity, string Where)> named)
    {
        var known = app.EntityList.Where(e => e.Key is not null).Select(e => e.Key!)
            .ToHashSet(StringComparer.Ordinal);

        return named
            // A null entity is absence, not a wrong answer — the gate reports a missing required
            // property far better than a second validator guessing at intent would.
            .Where(x => x.Entity is not null && !known.Contains(x.Entity))
            .Select(x => new CordError(CordErrorCode.UnknownEntity, x.Where,
                $"there is no entity '{x.Entity}'"))
            .ToList();
    }

    /// <summary>
    /// A lifecycle's own consistency, limited to what the AUTHOR could not otherwise see.
    ///
    /// <para>Two checks and no more. The entity must exist, and a transition must not move to a state
    /// the lifecycle does not declare — the second because Cord lets the author write states and
    /// transitions in ONE statement, so a typo there is a Cord-vocabulary error rather than a document
    /// one, and naming it at the operation index costs a round the gate's pointer would not save.</para>
    /// </summary>
    public static IReadOnlyList<CordError> LifecycleErrors(CordApp app, CordProcess process)
    {
        var errors = new List<CordError>(
            BehaviourEntityErrors(app, [(process.Entity, $"lifecycle/{process.Entity}")]));

        var states = process.StateList.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        if (states.Count == 0) return errors;

        // TWO CLAIMS ABOUT WHERE A RECORD STARTS. When the governed field's default AGREES with the
        // lifecycle's initialState it is one fact said twice, and CordLower drops the duplicate in
        // silence — the same treatment governed OPTIONS get. When they DISAGREE, dropping either one
        // would be Cord deciding something the author did not, so it is named instead.
        if (process.InitialState is { } initial && process.Entity is { } pe && process.StateField is { } sf)
        {
            var field = app.EntityList.FirstOrDefault(e => e.Key == pe)?.FieldList
                .FirstOrDefault(f => f.Key == sf);
            if (field?.Default is JsonValue v && v.TryGetValue<string>(out var dflt) && dflt != initial)
                errors.Add(new CordError(CordErrorCode.ConflictingInitialState,
                    $"lifecycle/{pe}",
                    $"this lifecycle starts a record in '{initial}', but '{pe}.{sf}' defaults to "
                    + $"'{dflt}'. Both say where a record begins and they disagree — set the one you "
                    + "mean and drop the other."));
        }

        foreach (var t in process.TransitionList)
        {
            if (t.To is { } to && !states.Contains(to))
                errors.Add(new CordError(CordErrorCode.UnknownState,
                    $"lifecycle/{process.Entity}/{t.Key}",
                    $"'{t.Key}' moves to '{to}', which is not one of this lifecycle's states "
                    + $"({string.Join(", ", states)})"));

            foreach (var from in t.FromList.Where(f => !states.Contains(f)))
                errors.Add(new CordError(CordErrorCode.UnknownState,
                    $"lifecycle/{process.Entity}/{t.Key}",
                    $"'{t.Key}' moves from '{from}', which is not one of this lifecycle's states "
                    + $"({string.Join(", ", states)})"));
        }

        return errors;
    }
}

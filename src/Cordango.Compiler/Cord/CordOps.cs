// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Cord;

/// <summary>
/// One semantic change to an application.
///
/// <para><b>Every operation names ONE aggregate and identifies it by a stable key.</b> That is the
/// shape decision this vocabulary is built on, and it was arrived at by getting it wrong twice in both
/// directions.</para>
///
/// <para><i>Too broad</i> was the first version: <c>replace_domain</c>, <c>define_roles</c>,
/// <c>define_screens</c> — operations that restated a whole collection. They read as economical (one
/// call, eleven entities) but they cannot express "change this one thing and leave the rest", so every
/// later edit had to resend everything that was already right, and a model that forgot one item
/// silently deleted it. In an architecture whose central bug was losing accumulated work, an operation
/// whose default behaviour is to discard accumulated work is the wrong default.</para>
///
/// <para><i>Too fine</i> is the other cliff: <c>add_transition</c>, <c>add_screen_section</c>,
/// <c>add_role_grant</c>. Those turn one thought into nine calls and put the burden of assembling an
/// aggregate back on the author — and if each became its own tool, the huge prompt would simply have
/// become a huge tool catalog.</para>
///
/// <para><b>The aggregate is the unit.</b> A lifecycle carries its states and transitions; a role
/// carries its grants; an automation carries its trigger and effects; a screen carries its sections.
/// Those are the things a person can hold in mind at once, and therefore the things a model can author
/// and repair without touching anything unrelated. Bulk is still free where it matters: <c>ops</c> is
/// an array, so eleven entities is eleven <c>upsert_entity</c> operations in a SINGLE call — the same
/// bytes on the wire as a collection-replacing op, without the destruction.</para>
/// </summary>
public abstract record CordOp;

/// <summary>Add an entity, or replace the one with this key. The blank page and the fiftieth edit are
/// the same operation, which is why there is no separate <c>add_</c>/<c>define_</c> pair.</summary>
public sealed record UpsertEntity(CordEntity Entity) : CordOp;

/// <summary>Add a field to an entity, or replace the one with this key. A REPLACE of that one field,
/// never a merge — merging would make clearing a property impossible to express — and never a touch of
/// any other field.</summary>
public sealed record UpsertField(string Entity, CordField Field) : CordOp;

public sealed record RemoveEntity(string Entity) : CordOp;
public sealed record RemoveField(string Entity, string Field) : CordOp;

// THERE IS NO `rename`, AND ITS ABSENCE IS THE DELIBERATE PART.
//
// It existed and it was wrong: it changed the key and rewrote nothing that pointed at it — not
// reference targets, not `expr` operands, not an aggregate's `of`/`over`, not a lifecycle's
// `stateField`, not a role's granted commands, not a screen section's filter fields. Every one of those
// then names something that is gone.
//
// The gate CATCHES that, so it was never silent corruption; it was worse in a subtler way. One rename
// came back as a scatter of unrelated-looking failures across the whole document, and repairing them
// by hand is exactly the low-level bookkeeping this layer exists to abolish. An operation that turns
// one intention into six errors is not an authoring surface.
//
// It is also not needed to CREATE anything, which is what the surface is for right now: an upsert
// states a thing in full, so getting a key right the first time costs nothing. It comes back when it
// can rewrite references — that is a real feature with a real test, not a line in a schema.

// ---- screens and behaviour -----------------------------------------------------------------------

/// <summary>
/// The screens people navigate to, each a question answered by sections.
///
/// <para><b>This started as a stopgap and stopped being one.</b> The first version was
/// <c>{entity, label, view}</c> — one screen, one entity — written only so a Cord app could pass the
/// pipeline's refusal to publish a definition with no pages. Measuring the corpus appeared to justify
/// it: 53 of 60 pages reference exactly one entity.</para>
///
/// <para>That measurement was misleading, and catching it is the reason this op was rebuilt. <b>Every
/// corpus app with a multi-entity page is one a person designed</b> — room_booking, ventures,
/// budget_planner, timesheets — <b>and all eleven the generator produced have none.</b> The corpus was
/// describing the generator's ceiling, not what applications need. An authoring surface derived from it
/// would have made "Investor Overview" (four entities, four metrics, a comparison and a chart)
/// permanently unauthorable while the runtime rendered it perfectly well.</para>
///
/// <para>It is still not a block tree, and there is still no way to author one through it. What changed
/// is that the ENTITY moved from the screen to the section.</para>
/// </summary>
public sealed record UpsertScreen(CordScreen Screen) : CordOp;

/// <summary>Drop a screen by key.</summary>
public sealed record RemoveScreen(string Key) : CordOp;

/// <summary>
/// Write one named tab of one screen, leaving the screen's other tabs and its shared sections exactly
/// as they were.
///
/// <para><b>Why this exists when <see cref="UpsertScreen"/> could say the same thing.</b> It could not,
/// safely. A whole-screen upsert carries every tab, so changing the cost tab means restating the three
/// the user already accepted — and an operation that restates accepted work can silently lose it to a
/// model's paraphrase. A named tab has a stable key and its own review, which makes it a child
/// aggregate exactly like a field, and <c>upsert_field</c> is the precedent this follows.</para>
///
/// <para>The line is at NAMED things: a tab has a key an author gave it. There is no
/// <c>upsert_block</c> and no positional addressing, because a block has no identity to be revised
/// against.</para>
/// </summary>
public sealed record UpsertScreenTab(string Screen, CordTab Tab) : CordOp;

/// <summary>Drop one named tab from a screen.</summary>
public sealed record RemoveScreenTab(string Screen, string Tab) : CordOp;

/// <summary>The states one entity moves through, and what happens on each move. Keyed by ENTITY rather
/// than by a lifecycle name, because an entity has exactly one — the key is a fact about the model, not
/// something the author has to keep consistent.</summary>
public sealed record UpsertLifecycle(CordProcess Process) : CordOp;

/// <summary>One thing people can do to a record that is not a state change.</summary>
public sealed record UpsertAction(CordAction Action) : CordOp;

/// <summary>One piece of work that happens on its own, with its trigger and its effects.</summary>
public sealed record UpsertAutomation(CordSchedule Schedule) : CordOp;

/// <summary>One role, with its grants.</summary>
public sealed record UpsertRole(CordRole Role) : CordOp;

/// <summary>Drop one behaviour aggregate. <paramref name="Kind"/> is one of
/// <see cref="CordBehaviourKinds.All"/>; for <c>lifecycle</c> the key is the ENTITY, matching how
/// <see cref="UpsertLifecycle"/> identifies it.</summary>
public sealed record RemoveBehaviour(string Kind, string Key) : CordOp;

/// <summary>The behaviour aggregates, named once so the schema's enum, the parser and the remover
/// cannot disagree about what is removable.</summary>
public static class CordBehaviourKinds
{
    public const string Lifecycle = "lifecycle";
    public const string Action = "action";
    public const string Automation = "automation";
    public const string Role = "role";

    public static readonly IReadOnlyList<string> All = [Lifecycle, Action, Automation, Role];
}

/// <summary>
/// The domain operations, and the schema Claude actually sees.
///
/// <para><b>Rule 3: concern-scoped.</b> <see cref="CordApp"/> may model the whole application and Cord
/// may internally support any number of operations, but the model receives only the vocabulary for the
/// task in front of it. Behaviour and UI get their own schemas in later slices; this one never grows
/// to include them. The failure being avoided is precise and the repo has already lived it once — the
/// screen tool schema's byte ceilings in <c>ComposeDesignTests</c> carry a ledger entry reading
/// <i>"RAISED IN BULK, AND THIS ONE IS A PROBLEM … the narrowing pass the note above demands is now
/// owed, not optional"</i>. A semantic API that accretes operations until it is a 60 KB union has
/// reproduced exactly the problem it was built to remove.</para>
///
/// <para><b>Provider-neutral.</b> <see cref="DomainOpsSchema"/> returns plain JSON Schema. Turning it
/// into a tool definition — a name, a description, an <c>input_schema</c> — is
/// <c>CordToolAdapter</c>'s job in the Generator. Cord never names a vendor, a tool or a model, which
/// is what keeps the same operations reachable from an MCP server or a CLI later.</para>
/// </summary>
public static class CordOps
{
    /// <summary>
    /// What the model may say about the DATA MODEL. Kept as data so the schema and the parser cannot
    /// disagree about which operations exist.
    ///
    /// <para><b>Three, down from seven.</b> <c>replace_domain</c>, <c>define_entities</c> and
    /// <c>add_entity</c> were three ways to say the same thing that differed only in what they destroyed
    /// on the way; <c>add_field</c>/<c>set_field</c> were two ways to say a field, distinguished by
    /// whether it already existed. An author who has to know which of three creation operations applies
    /// is being asked a question about the API rather than about their application. <c>rename</c> went
    /// for a different reason — see the note above its former declaration.</para>
    /// </summary>
    public static readonly IReadOnlyList<string> DomainOpNames =
    [
        "upsert_entity", "upsert_field", "remove",
    ];

    /// <summary>
    /// What the model may say about BEHAVIOUR. A separate list, reachable only from
    /// <see cref="BehaviourOpsSchema"/> — rule 3.
    ///
    /// <para>One upsert per aggregate — lifecycle, action, automation, role — plus one removal. Each
    /// carries the whole aggregate (a lifecycle its states and transitions, a role its grants) and
    /// nothing outside it, so adding a second role no longer means restating the first.</para>
    ///
    /// <para>There is no <c>upsert_command</c>. A command that belongs to a state change is authored as
    /// part of the lifecycle, and one that does not is an action — which is the shape decision of this
    /// concern, enforced by there being no third way to say it.</para>
    /// </summary>
    public static readonly IReadOnlyList<string> BehaviourOpNames =
    [
        "upsert_lifecycle", "upsert_action", "upsert_automation", "upsert_role", "remove_behaviour",
    ];

    /// <summary>
    /// What the model may say about SCREENS.
    ///
    /// <para>These moved out of the domain set: a screen used to be <c>{entity, label, view}</c> and
    /// could only ever show one entity, which made a real overview page — four metrics, a comparison and
    /// a chart across four entities — unauthorable. Giving it sections took the domain schema past its
    /// ceiling, and rule 3's answer to that is to split the concern rather than raise the bound.</para>
    /// </summary>
    /// <para>The tab pair is here rather than in a concern of its own because a tab is part of the
    /// screen it belongs to: the model authoring a screen is the one that needs to revise its tabs, and
    /// splitting them would put one aggregate's vocabulary behind two tools.</para>
    public static readonly IReadOnlyList<string> UiOpNames =
        ["upsert_screen", "remove_screen", "upsert_screen_tab", "remove_screen_tab"];

    /// <summary>Every operation Cord has, across all concerns. Not a model-facing set — rule 3 hands out
    /// concerns one at a time — but the parser needs to tell "no such operation" apart from "not on this
    /// tool", and those are different answers.</summary>
    public static readonly IReadOnlyList<string> AllOpNames =
        [.. DomainOpNames, .. BehaviourOpNames, .. UiOpNames];

    /// <summary>
    /// The schema one operation is validated against — the authority for what a caller may send.
    ///
    /// <para><b>Exposed because it was needed and unreachable.</b> An agent working through the CLI
    /// discovered this vocabulary by submitting deliberately-invalid operations and reading the
    /// rejections, which is reverse-engineering a specification that already existed. The GENERATOR
    /// stays internal — it is an implementation detail of how the tool schemas are composed — but its
    /// result is a contract, and a contract nobody can read is one everybody guesses at.</para>
    /// </summary>
    /// <returns>Null when no operation has that name.</returns>
    public static JsonObject? SchemaFor(string name) => CordOpsSchema.ForOp(name);

    // ---- parsing ---------------------------------------------------------------------------------

    /// <summary>
    /// Reads a batch of operations. Never throws: a malformed operation becomes a
    /// <see cref="CordErrorCode.MalformedOperation"/> naming its INDEX, so the model is told which of
    /// its five operations was wrong rather than that the batch failed.
    /// </summary>
    /// <param name="allowed">The operation names this call may use. <b>Load-bearing, not decorative.</b>
    /// Without it every tool accepted every operation, so <c>apply_domain_changes</c> would happily take
    /// a <c>define_lifecycle</c> — which quietly undoes rule 3: concern-scoped SCHEMAS mean nothing if
    /// the parser accepts the union anyway, and a model that guessed at vocabulary it was never offered
    /// would be rewarded for it. Null means every operation, for callers that are not a concern-scoped
    /// tool (tests, and a future MCP surface that deliberately exposes all of them).</param>
    public static (IReadOnlyList<CordOp> Ops, IReadOnlyList<CordError> Errors) Parse(
        JsonNode? ops, IReadOnlyCollection<string>? allowed = null)
    {
        var parsed = new List<CordOp>();
        var errors = new List<CordError>();

        if (ops is not JsonArray array)
        {
            errors.Add(new CordError(CordErrorCode.MalformedOperation, "ops",
                "`ops` must be an array of operations"));
            return (parsed, errors);
        }

        // ---- 1. THE NAME, first and on its own -------------------------------------------------------
        //
        // Ahead of the schema deliberately. A schema failure on an unrecognised `op` arrives as a
        // handful of oneOf branch errors saying the const did not match, which tells the author nothing
        // they can act on. Answering the name question here keeps the message that matters: this
        // operation exists, and it lives on the behaviour tool.
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonObject op)
            {
                errors.Add(new CordError(CordErrorCode.MalformedOperation, "ops", "not an object", i));
                continue;
            }

            var named = Str(op, "op");
            if (named is null)
                errors.Add(new CordError(CordErrorCode.MalformedOperation, "ops", "`op` is required", i));
            else if (!AllOpNames.Contains(named))
                errors.Add(new CordError(CordErrorCode.MalformedOperation, "ops",
                    $"'{named}' is not an operation — expected one of {string.Join(", ", AllOpNames)}", i));
            else if (allowed is not null && !allowed.Contains(named))
                errors.Add(new CordError(CordErrorCode.MalformedOperation, "ops",
                    $"'{named}' is not an operation this tool accepts — it takes "
                    + $"{string.Join(", ", allowed)}. {Elsewhere(named)}", i));
        }

        if (errors.Count > 0) return (parsed, errors);

        // ---- 2. THE SHAPE, against the schema the model was given ------------------------------------
        //
        // Without this the readers below took what they recognised and dropped the rest in silence: an
        // `ask` written as {title, fields:[{key,label,type}]} — the shape a model would reasonably
        // invent — was accepted, emptied, and the app shipped with a prompt that collects nothing.
        // Found by hand-authoring three applications and writing exactly that.
        //
        // In a live session the provider validates tool arguments too, so this looks redundant there
        // and is not: an MCP client or a CLI reaches Parse with no provider in between, and "accepted
        // then discarded" is the worst answer an API can give. The GENERATED schema, never a second
        // hand-written property list, so the two cannot drift apart.
        for (var i = 0; i < array.Count; i++)
        {
            if (CordOpsSchema.ForOp(Str((JsonObject)array[i]!, "op")!) is not { } schema) continue;
            foreach (var problem in Definition.Gate.StructuralErrors(array[i], schema))
                errors.Add(Structural(problem, i));
        }

        // Atomic: one misplaced property refuses the WHOLE batch, so a half-understood change never
        // becomes half of an application.
        if (errors.Count > 0) return (parsed, errors);

        // ---- 3. THE MEANING --------------------------------------------------------------------------
        for (var i = 0; i < array.Count; i++)
        {
            var op = (JsonObject)array[i]!;
            var name = Str(op, "op");
            CordOp? result = null;
            string? problem = null;

            switch (name)
            {
                case "upsert_entity":
                    if (op["entity"] is JsonObject e) result = new UpsertEntity(CordWire.Entity(e));
                    else problem = "`entity` is required";
                    break;
                case "upsert_field":
                {
                    var entity = Str(op, "entity");
                    if (entity is null) { problem = "`entity` is required"; break; }
                    if (op["field"] is not JsonObject f) { problem = "`field` is required"; break; }
                    result = new UpsertField(entity, CordWire.Field(f));
                    break;
                }
                case "remove":
                {
                    var entity = Str(op, "entity");
                    if (entity is null) { problem = "`entity` is required"; break; }
                    result = Str(op, "field") is { } field
                        ? new RemoveField(entity, field)
                        : new RemoveEntity(entity);
                    break;
                }
                case "upsert_screen":
                {
                    if (op["screen"] is not JsonObject s) { problem = "`screen` is required"; break; }
                    result = new UpsertScreen(CordWireScreens.Screen(s));
                    break;
                }
                case "remove_screen":
                {
                    if (Str(op, "key") is not { } key) { problem = "`key` is required"; break; }
                    result = new RemoveScreen(key);
                    break;
                }
                case "upsert_screen_tab":
                {
                    if (Str(op, "screen") is not { } target) { problem = "`screen` is required"; break; }
                    if (op["tab"] is not JsonObject t) { problem = "`tab` is required"; break; }
                    var tab = CordWireScreens.Tab(t);
                    // A tab with no key has no identity, so nothing could revise it later — refused
                    // here rather than lowered into an unaddressable tab.
                    if (tab.Key.Length == 0) { problem = "`tab.key` is required"; break; }
                    result = new UpsertScreenTab(target, tab);
                    break;
                }
                case "remove_screen_tab":
                {
                    if (Str(op, "screen") is not { } from) { problem = "`screen` is required"; break; }
                    if (Str(op, "tab") is not { } which) { problem = "`tab` is required"; break; }
                    result = new RemoveScreenTab(from, which);
                    break;
                }
                case "upsert_lifecycle":
                {
                    if (op["lifecycle"] is not JsonObject l) { problem = "`lifecycle` is required"; break; }
                    result = new UpsertLifecycle(CordWireBehaviour.Process(l));
                    break;
                }
                case "upsert_action":
                {
                    if (op["action"] is not JsonObject a) { problem = "`action` is required"; break; }
                    result = new UpsertAction(CordWireBehaviour.Action(a));
                    break;
                }
                case "upsert_automation":
                {
                    if (op["automation"] is not JsonObject a) { problem = "`automation` is required"; break; }
                    result = new UpsertAutomation(CordWireBehaviour.Schedule(a));
                    break;
                }
                case "upsert_role":
                {
                    if (op["role"] is not JsonObject r) { problem = "`role` is required"; break; }
                    result = new UpsertRole(CordWireBehaviour.Role(r));
                    break;
                }
                case "remove_behaviour":
                {
                    var kind = Str(op, "kind");
                    var key = Str(op, "key");
                    if (kind is null || key is null) { problem = "`kind` and `key` are required"; break; }
                    if (!CordBehaviourKinds.All.Contains(kind))
                    {
                        problem = $"'{kind}' is not a behaviour kind — expected one of "
                            + string.Join(", ", CordBehaviourKinds.All);
                        break;
                    }
                    result = new RemoveBehaviour(kind, key);
                    break;
                }
                default:
                    // Unreachable: pass 1 rejected every name that is not in AllOpNames.
                    problem = $"'{name}' is not an operation";
                    break;
            }

            if (result is not null) parsed.Add(result);
            else errors.Add(new CordError(CordErrorCode.MalformedOperation, "ops", problem ?? "unreadable", i));
        }

        return (parsed, errors);

        // A schema failure, rewritten into Cord's own vocabulary. The gate reports
        // `STRUCTURAL at [/2/entity/fields/0/colour]: …`; the author needs the OPERATION INDEX, which
        // is that pointer's first segment, and the property, which is its last.
        static CordError Structural(string message, int index)
        {
            var open = message.IndexOf('[');
            var close = message.IndexOf(']');
            var pointer = open >= 0 && close > open ? message[(open + 1)..close] : "";
            var detail = close >= 0 && close + 2 < message.Length ? message[(close + 2)..].Trim() : message;
            // The schema library's phrasing for additionalProperties:false is "All values fail against
            // the false schema", which tells an author nothing. The POINTER already names the property,
            // so say what is actually wrong with it.
            if (detail.Contains("false schema", StringComparison.Ordinal))
                detail = "there is no such property here";

            return new CordError(CordErrorCode.MalformedOperation,
                pointer.Length > 0 ? pointer.TrimStart('/') : "ops", detail, index);
        }

        // Which tool DOES take it. A bare refusal would send the model hunting; naming the right tool
        // turns a wasted round into a corrected call.
        static string Elsewhere(string name) =>
            DomainOpNames.Contains(name) ? "It belongs to the data-model tool."
            : BehaviourOpNames.Contains(name) ? "It belongs to the behaviour tool."
            : UiOpNames.Contains(name) ? "It belongs to the screens tool."
            : "";
    }

    // ---- application -----------------------------------------------------------------------------

    /// <summary>
    /// Applies operations in order to a COPY of the draft.
    ///
    /// <para>Order matters and is honoured literally: an <c>upsert_field</c> after the
    /// <c>upsert_entity</c> that created its entity finds it, in the same batch. Nothing here validates
    /// — that is <see cref="CordCheck"/>'s job on the result, and keeping the two apart is what lets a
    /// batch be judged as a whole rather than operation by operation.</para>
    /// </summary>
    public static (CordApp App, IReadOnlyList<CordError> Errors) Apply(CordApp draft, IReadOnlyList<CordOp> ops)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(ops);

        var entities = draft.EntityList.ToList();
        var screens = draft.Screens?.ToList();
        // Cloned rather than shared because the screen arms READ it to decide whether this app carries
        // pages Cord cannot see — see CarriedScreens.
        var rootRaw = draft.Raw is null ? null : (JsonObject)draft.Raw.DeepClone();
        var processes = draft.Processes?.ToList();
        var actions = draft.Actions?.ToList();
        var schedules = draft.Schedules?.ToList();
        var roles = draft.Roles?.ToList();
        var errors = new List<CordError>();

        for (var i = 0; i < ops.Count; i++)
        {
            switch (ops[i])
            {
                case UpsertEntity ue:
                {
                    var at = entities.FindIndex(x => x.Key == ue.Entity.Key);
                    if (at < 0) entities.Add(ue.Entity);
                    else entities[at] = ue.Entity;
                    break;
                }

                case UpsertField uf:
                    Edit(uf.Entity, i, e =>
                    {
                        var fields = e.FieldList.ToList();
                        var at = fields.FindIndex(x => x.Key == uf.Field.Key);
                        // A REPLACE of this one field, not a merge: merging would make clearing a
                        // property impossible to express. Nothing else on the entity is touched.
                        if (at < 0) fields.Add(uf.Field);
                        else fields[at] = uf.Field;
                        return e with { Fields = fields };
                    });
                    break;

                case RemoveEntity rme:
                    if (entities.RemoveAll(x => x.Key == rme.Entity) == 0)
                        errors.Add(new CordError(CordErrorCode.UnknownEntity, $"entities/{rme.Entity}",
                            $"there is no entity '{rme.Entity}' to remove", i));
                    break;

                case RemoveField rmf:
                    Edit(rmf.Entity, i, e =>
                    {
                        var fields = e.FieldList.ToList();
                        if (fields.RemoveAll(x => x.Key == rmf.Field) == 0)
                            errors.Add(new CordError(CordErrorCode.UnknownField,
                                $"entities/{rmf.Entity}/fields/{rmf.Field}",
                                $"'{rmf.Entity}' has no field '{rmf.Field}'", i));
                        return e with { Fields = fields };
                    });
                    break;

                case UpsertScreen us:
                {
                    if (CarriedScreens(i)) break;

                    screens ??= [];
                    var at = screens.FindIndex(s => s.Key == us.Screen.Key);
                    if (at < 0) screens.Add(us.Screen);
                    else screens[at] = us.Screen;

                    errors.AddRange(CordCheck
                        .ScreenErrors(draft with { Entities = entities }, [us.Screen])
                        .Select(e => e with { OperationIndex = i }));
                    break;
                }

                case RemoveScreen rs:
                {
                    if (CarriedScreens(i)) break;

                    if (screens is null || screens.RemoveAll(s => s.Key == rs.Key) == 0)
                        errors.Add(new CordError(CordErrorCode.UnknownScreen, $"screens/{rs.Key}",
                            $"there is no screen '{rs.Key}' to remove", i));
                    break;
                }

                case UpsertScreenTab ut:
                {
                    if (CarriedScreens(i)) break;

                    var at = screens?.FindIndex(s => s.Key == ut.Screen) ?? -1;
                    if (at < 0)
                    {
                        errors.Add(new CordError(CordErrorCode.UnknownScreen, $"screens/{ut.Screen}",
                            $"there is no screen '{ut.Screen}' to put a tab on", i));
                        break;
                    }

                    // Replace in place, append if new: the tab's key is its identity, and the ORDER of
                    // the others is the author's statement about which perspective comes first.
                    var host = screens![at];
                    var tabs = host.TabList.ToList();
                    var slot = tabs.FindIndex(t => t.Key == ut.Tab.Key);
                    if (slot < 0) tabs.Add(ut.Tab); else tabs[slot] = ut.Tab;
                    screens[at] = host with { Tabs = tabs };

                    errors.AddRange(CordCheck
                        .ScreenErrors(draft with { Entities = entities }, [screens[at]])
                        .Select(e => e with { OperationIndex = i }));
                    break;
                }

                case RemoveScreenTab rt:
                {
                    if (CarriedScreens(i)) break;

                    var at = screens?.FindIndex(s => s.Key == rt.Screen) ?? -1;
                    if (at < 0)
                    {
                        errors.Add(new CordError(CordErrorCode.UnknownScreen, $"screens/{rt.Screen}",
                            $"there is no screen '{rt.Screen}'", i));
                        break;
                    }

                    var host = screens![at];
                    var tabs = host.TabList.ToList();
                    if (tabs.RemoveAll(t => t.Key == rt.Tab) == 0)
                    {
                        errors.Add(new CordError(CordErrorCode.UnknownTab,
                            $"screens/{rt.Screen}/tabs/{rt.Tab}",
                            $"screen '{rt.Screen}' has no tab '{rt.Tab}' to remove", i));
                        break;
                    }
                    screens[at] = host with { Tabs = tabs.Count == 0 ? null : tabs };
                    break;
                }

                case UpsertLifecycle ul:
                {
                    processes ??= [];
                    // Keyed by ENTITY, not by the lifecycle's own name: an entity has one lifecycle, so
                    // the identity is a fact about the model rather than something the author has to
                    // keep consistent between calls.
                    var at = processes.FindIndex(p => p.Entity == ul.Process.Entity);
                    if (at >= 0) processes[at] = ul.Process;
                    else processes.Add(ul.Process);
                    errors.AddRange(CordCheck.LifecycleErrors(draft with { Entities = entities }, ul.Process)
                        .Select(e => e with { OperationIndex = i }));
                    break;
                }

                case UpsertAction ua:
                {
                    actions ??= [];
                    var at = actions.FindIndex(a => a.Key == ua.Action.Key);
                    if (at < 0) actions.Add(ua.Action);
                    else actions[at] = ua.Action;
                    errors.AddRange(CordCheck.BehaviourEntityErrors(
                            draft with { Entities = entities },
                            [(ua.Action.Entity, $"actions/{ua.Action.Key}")])
                        .Select(e => e with { OperationIndex = i }));
                    break;
                }

                case UpsertAutomation um:
                {
                    schedules ??= [];
                    var at = schedules.FindIndex(s => s.Key == um.Schedule.Key);
                    if (at < 0) schedules.Add(um.Schedule);
                    else schedules[at] = um.Schedule;
                    errors.AddRange(CordCheck.BehaviourEntityErrors(
                            draft with { Entities = entities },
                            [(um.Schedule.Trigger?.Entity, $"automations/{um.Schedule.Key}")])
                        .Select(e => e with { OperationIndex = i }));
                    break;
                }

                case UpsertRole ur:
                {
                    roles ??= [];
                    var at = roles.FindIndex(r => r.Key == ur.Role.Key);
                    if (at < 0) roles.Add(ur.Role);
                    else roles[at] = ur.Role;
                    errors.AddRange(CordCheck.BehaviourEntityErrors(
                            draft with { Entities = entities },
                            // `*` is EVERY entity, not an entity called "*". All 15 corpus apps carry
                            // one — an administrator granted the whole application — and until this
                            // exclusion the check read it as a reference and refused it, so the one
                            // grant every reference app has was the one grant Cord could not write.
                            ur.Role.GrantList.Where(g => g.Entity != CordGrant.EveryEntity)
                                .Select(g => (g.Entity, $"roles/{ur.Role.Key}")))
                        .Select(e => e with { OperationIndex = i }));
                    break;
                }

                case RemoveBehaviour rb:
                {
                    var removed = rb.Kind switch
                    {
                        CordBehaviourKinds.Lifecycle => processes?.RemoveAll(p => p.Entity == rb.Key) ?? 0,
                        CordBehaviourKinds.Action => actions?.RemoveAll(a => a.Key == rb.Key) ?? 0,
                        CordBehaviourKinds.Automation => schedules?.RemoveAll(s => s.Key == rb.Key) ?? 0,
                        _ => roles?.RemoveAll(r => r.Key == rb.Key) ?? 0,
                    };
                    if (removed == 0)
                        errors.Add(new CordError(CordErrorCode.UnknownBehaviour, $"{rb.Kind}/{rb.Key}",
                            $"there is no {rb.Kind} '{rb.Key}' to remove", i));
                    break;
                }
            }
        }

        return (draft with
        {
            Entities = entities,
            Screens = screens,
            Processes = processes,
            Actions = actions,
            Schedules = schedules,
            Roles = roles,
            // Null when emptied, so an exhausted overlay disappears rather than lowering as `{}`.
            Raw = rootRaw is { Count: > 0 } ? rootRaw : null,
        }, errors);

        // AUTHORING SCREENS OVER AN APP WHOSE SCREENS CORD CANNOT SEE IS REFUSED, and the history here
        // is worth keeping because the wrong fix shipped twice.
        //
        // An imported app whose pages are outside Cord's vocabulary carries them in the ROOT OVERLAY.
        // CordLower emits Cord's screens and then merges that overlay over the top, arrays
        // replacing wholesale. So at first a screen change on a resumed draft was accepted with zero
        // errors, reported as applied, and then completely discarded — the author is told the screen
        // exists and the app still has the old ones. The second attempt dropped the overlay instead,
        // which fixed the lie by deleting every imported page: quieter, and worse.
        //
        // There is no correct MERGE available. Joining the two sets needs keys on both sides and only
        // one side has them. When the only two implementable behaviours are "your change vanishes" and
        // "their screens vanish", the operation cannot be honoured, and saying so is the honest answer:
        // a refusal is visible, names the missing capability, and cannot destroy anything.
        //
        // This lifts when CordImport models pages. Until then it is a WALL, deliberately — plan risk 3:
        // an unmodelled thing must be a wall rather than a slow leak, because a wall shows up as a
        // named refusal and a leak shows up as silent data loss six months later.
        bool CarriedScreens(int index)
        {
            if (rootRaw?["pages"] is null && rootRaw?["views"] is null) return false;

            errors.Add(new CordError(CordErrorCode.ImportedScreensNotEditable, "screens",
                "this app already has screens that were not authored through Cord, and there is no way "
                + "to change one without discarding the others. Leave the screens as they are.", index));
            return true;
        }

        void Edit(string key, int index, Func<CordEntity, CordEntity> change)
        {
            var at = entities.FindIndex(x => x.Key == key);
            if (at < 0)
            {
                errors.Add(new CordError(CordErrorCode.UnknownEntity, $"entities/{key}",
                    $"there is no entity '{key}'", index));
                return;
            }
            entities[at] = change(entities[at]);
        }
    }

    private static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    // ---- the model-facing schema -----------------------------------------------------------------

    /// <summary>The domain authoring vocabulary as provider-neutral JSON Schema.</summary>
    public static JsonObject DomainOpsSchema() => CordOpsSchema.Domain();

    /// <summary>The behaviour authoring vocabulary, as its OWN schema.
    ///
    /// <para>Rule 3: concerns are handed out separately and never unioned. A session authoring
    /// behaviour is given this and not the domain vocabulary — it is editing a draft it can
    /// <c>inspect</c>, so it does not need the entity schema to talk about entities.</para></summary>
    public static JsonObject BehaviourOpsSchema() => CordOpsSchemaBehaviour.Behaviour();

    /// <summary>The screen authoring vocabulary, as its own schema. See <see cref="UiOpNames"/> for why
    /// it is not part of the domain's.</summary>
    public static JsonObject UiOpsSchema() => CordOpsSchemaUi.Ui();

    /// <summary>
    /// Schema size, per plan §5 — <b>bytes, estimated tokens, operation count, nesting depth</b>.
    ///
    /// <para>Printed by the tests and reported by the benchmark so the context claim is auditable
    /// rather than asserted. The comparison that matters is against <c>DesignTools.SubmitSchema()</c>:
    /// ~208 KB, ~52k tokens, ONE operation.</para>
    /// </summary>
    public static CordSchemaReport SchemaReport(string concern, JsonObject schema)
    {
        var json = schema.ToJsonString();
        var ops = (schema["properties"]?["ops"]?["items"]?["oneOf"] as JsonArray)?.Count ?? 0;
        return new CordSchemaReport(concern, json.Length, json.Length / 4, ops, Depth(schema));

        static int Depth(JsonNode? node) => node switch
        {
            JsonObject o => o.Count == 0 ? 1 : 1 + o.Max(p => Depth(p.Value)),
            JsonArray a => a.Count == 0 ? 1 : 1 + a.Max(Depth),
            _ => 0,
        };
    }
}

/// <param name="EstimatedTokens">bytes/4. An ESTIMATE, said out loud — no tokenizer runs here, and a
/// number that looks measured but is not would be worse than an honest approximation.</param>
public sealed record CordSchemaReport(string Concern, int Bytes, int EstimatedTokens, int Operations, int Depth)
{
    public override string ToString() =>
        $"{Concern,-24} {Bytes,8:N0} B  ~{EstimatedTokens,7:N0} tok  {Operations,3} ops  depth {Depth}";
}

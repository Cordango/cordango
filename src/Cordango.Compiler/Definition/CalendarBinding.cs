// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Definition;

/// <summary>
/// What an entity's <c>calendar</c> flag resolves to: which field says when, which says who, and how
/// the entry should read. Every slot here is either something the author stated or something derived
/// from what the entity already declares.
/// </summary>
/// <param name="Start">The date/datetime field an entry starts on.</param>
/// <param name="End">The field it runs until, or null for a single-day mark.</param>
/// <param name="Who">
/// The reference field naming the responsible person. When <see cref="WhoVia"/> is set this key is a
/// field on the PARENT entity, not on this one.
/// </param>
/// <param name="WhoVia">
/// The <c>ownedBy.via</c> reference to follow before reading <see cref="Who"/>, or null when the
/// person is on the entity itself. This is the hop that puts a milestone in the calendar of whoever
/// leads its project without the milestone carrying a person at all.
/// </param>
/// <param name="WhoEntity">The entity <see cref="Who"/> lives on — this entity, or the parent.</param>
/// <param name="Title">A field key or a <c>{{field}}</c> template.</param>
/// <param name="AllDay">Whole days rather than a time of day.</param>
/// <param name="StatusField">The role:'status' field the colour and state come from, if any.</param>
/// <param name="HideWhen">A condition whose matching records stay out of the calendar.</param>
public sealed record CalendarBinding(
    string Start,
    string? End,
    string Who,
    string? WhoVia,
    string WhoEntity,
    string Title,
    bool AllDay,
    string? StatusField,
    JsonObject? HideWhen)
{
    public JsonObject ToJson()
    {
        var o = new JsonObject { ["start"] = Start };
        if (End is not null) o["end"] = End;
        o["who"] = Who;
        if (WhoVia is not null) o["whoVia"] = WhoVia;
        o["whoEntity"] = WhoEntity;
        o["title"] = Title;
        o["allDay"] = AllDay;
        if (StatusField is not null) o["statusField"] = StatusField;
        if (HideWhen is not null) o["hideWhen"] = (JsonObject)HideWhen.DeepClone();
        return o;
    }
}

/// <summary>
/// Resolves an entity's <c>calendar</c> flag into a <see cref="CalendarBinding"/>.
///
/// <para><b>One derivation, two callers.</b> The Gate calls this to REFUSE a flag it cannot resolve,
/// and <c>AppCompiler</c> calls it to stamp the answer into the manifest. Written once because the
/// alternative is the clock-token failure: the same grammar implemented separately in the checker and
/// the builder, agreeing until they quietly do not.</para>
///
/// <para><b>Derive, never overwrite.</b> Every rule below fills a slot the author left EMPTY. An
/// authored key is returned untouched — validated, but never replaced. That is the distinction
/// against <c>ResolveBoardInteraction</c>, which disabled every process kanban by rewriting the
/// definition from a belief about what the renderer did.</para>
/// </summary>
public static class CalendarResolver
{
    /// <summary>
    /// The order to pick a person reference in when an entity carries several.
    ///
    /// <para>Not alphabetical and not arbitrary: it runs from the person the work is ASSIGNED to,
    /// through the person who owns it, to the person who merely filed it. A ticket with both an
    /// `assignee` and a `submitted_by` belongs in the assignee's calendar — the submitter's day is
    /// not changed by having asked.</para>
    /// </summary>
    public static readonly IReadOnlyList<string> WhoPrecedence =
    [
        "assignee", "owner", "lead", "responsible", "performed_by", "booked_by",
        "interviewed_by", "requested_by", "submitted_by", "logged_by",
    ];

    /// <summary>Is this entity opted in at all? `false` is a deliberate opt-OUT and reads the same as
    /// absent, so a definition can say no out loud.</summary>
    public static bool IsOptedIn(JsonObject entity) => entity["calendar"] switch
    {
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonObject => true,
        _ => false,
    };

    /// <summary>
    /// Resolve the flag. Returns the binding, or null plus the reasons it could not be resolved.
    ///
    /// <para>Errors are phrased as the Gate phrases them and always NAME the candidates: "no
    /// resolvable date" with nothing else said is the kind of refusal that sends an author reading
    /// the compiler instead of their own definition.</para>
    /// </summary>
    public static (CalendarBinding? Binding, List<string> Errors) Resolve(
        JsonArray entities, JsonObject entity)
    {
        var errors = new List<string>();
        if (!IsOptedIn(entity)) return (null, errors);

        var ekey = Str(entity, "key") ?? "";
        var cfg = entity["calendar"] as JsonObject;
        var fields = FieldsOf(entity);

        var start = ResolveStart(ekey, cfg, fields, errors);
        var end = ResolveEnd(ekey, cfg, fields, start, errors);
        var (who, whoVia, whoEntity) = ResolveWho(entities, entity, ekey, cfg, fields, errors);
        var title = Str(cfg, "title") ?? Str(entity, "displayField") ?? start ?? ekey;
        var status = fields.Values.FirstOrDefault(f => Str(f, "role") == "status") is { } sf
            ? Str(sf, "key") : null;

        // allDay follows the START field's own type unless stated: a `date` is a whole day by
        // construction, and a `datetime` placed on a whole-day strip loses the only thing it knew
        // that a date did not.
        var allDay = cfg?["allDay"] is JsonValue av && av.TryGetValue<bool>(out var ad)
            ? ad
            : start is not null && fields.TryGetValue(start, out var sfld) && Str(sfld, "type") != "datetime";

        ValidateHideWhen(ekey, cfg, fields, errors);

        if (start is null || who is null || errors.Count > 0) return (null, errors);
        return (new CalendarBinding(start, end, who, whoVia, whoEntity!, title, allDay, status,
            cfg?["hideWhen"] as JsonObject), errors);
    }

    private static string? ResolveStart(
        string ekey, JsonObject? cfg, Dictionary<string, JsonObject> fields, List<string> errors)
    {
        if (Str(cfg, "start") is { } authored)
        {
            if (!fields.TryGetValue(authored, out var f))
                errors.Add($"SEMANTIC: entity '{ekey}' has calendar.start '{authored}', which is not a field on it");
            else if (Str(f, "type") is not ("date" or "datetime"))
                errors.Add($"SEMANTIC: entity '{ekey}' has calendar.start '{authored}' but that field is a "
                         + $"'{Str(f, "type")}' — a calendar entry starts on a 'date' or 'datetime' field");
            return authored;
        }

        if (RoleField(fields, "start") is { } roleStart) return roleStart;
        if (RoleField(fields, "due") is { } roleDue) return roleDue;

        // System fields are excluded, and this is load-bearing rather than tidy: the Gate sees the
        // definition (no system fields yet) while the compiler sees the manifest, where every entity
        // has picked up `created_at`, `updated_at` and `deleted_at`. Counting those, "the single date
        // field" would never be single and this would refuse every entity in the corpus.
        var dates = fields.Values
            .Where(f => Str(f, "type") is "date" or "datetime" && !IsSystem(f))
            .Select(f => Str(f, "key")!).Where(k => k is not null).ToList();

        if (dates.Count == 1) return dates[0];
        if (dates.Count == 0)
        {
            errors.Add($"SEMANTIC: entity '{ekey}' is on the calendar but has no date field — "
                     + "a record with no date has no day to appear on");
            return null;
        }
        // The refusal that earns its keep. Most unroled dates are LEDGER dates, and guessing one of
        // several would put "invoice paid on" in somebody's week with no way to tell it was a guess.
        errors.Add($"SEMANTIC: entity '{ekey}' is on the calendar and has several date fields "
                 + $"({string.Join(", ", dates.OrderBy(d => d, StringComparer.Ordinal))}) with none marked "
                 + "role:'start' or role:'due' — name one as calendar.start, or tag it role:'start'");
        return null;
    }

    private static string? ResolveEnd(
        string ekey, JsonObject? cfg, Dictionary<string, JsonObject> fields, string? start, List<string> errors)
    {
        if (Str(cfg, "end") is { } authored)
        {
            if (!fields.TryGetValue(authored, out var f))
                errors.Add($"SEMANTIC: entity '{ekey}' has calendar.end '{authored}', which is not a field on it");
            else if (Str(f, "type") is not ("date" or "datetime"))
                errors.Add($"SEMANTIC: entity '{ekey}' has calendar.end '{authored}' but that field is a "
                         + $"'{Str(f, "type")}' — a calendar entry ends on a 'date' or 'datetime' field");
            else if (authored == start)
                errors.Add($"SEMANTIC: entity '{ekey}' has calendar.end '{authored}', the same field as its "
                         + "start — an entry that ends where it starts is a single day, so leave end out");
            return authored;
        }
        // Only when the start was the SEMANTIC start. If start fell back to role:'due', that due date
        // IS the mark; making it both ends of a span would draw a zero-length bar.
        return start is not null && start == RoleField(fields, "start") ? RoleField(fields, "due") : null;
    }

    private static (string? Who, string? Via, string? Entity) ResolveWho(
        JsonArray entities, JsonObject entity, string ekey, JsonObject? cfg,
        Dictionary<string, JsonObject> fields, List<string> errors)
    {
        var ownedBy = entity["ownedBy"] as JsonObject;
        var parentKey = Str(ownedBy, "parent");
        var via = Str(ownedBy, "via");
        var parent = parentKey is null ? null : EntityByKey(entities, parentKey);
        var parentFields = parent is null ? null : FieldsOf(parent);

        if (Str(cfg, "who") is { } authored)
        {
            // An authored `who` may name a field on this entity OR one on the parent — the second is
            // how an author overrides which of a parent's several people owns the entry.
            if (fields.TryGetValue(authored, out var own))
            {
                if (!IsPersonRef(own))
                    errors.Add($"SEMANTIC: entity '{ekey}' has calendar.who '{authored}', which is not a "
                             + "reference to the platform person directory — a calendar entry belongs to a person");
                return (authored, null, ekey);
            }
            if (via is not null && parentFields is not null && parentFields.TryGetValue(authored, out var up))
            {
                if (!IsPersonRef(up))
                    errors.Add($"SEMANTIC: entity '{ekey}' has calendar.who '{authored}' on its parent "
                             + $"'{parentKey}', which is not a reference to the platform person directory");
                return (authored, via, parentKey);
            }
            errors.Add($"SEMANTIC: entity '{ekey}' has calendar.who '{authored}', which is not a field on it"
                     + (parentKey is null ? "" : $" or on its parent '{parentKey}'"));
            return (authored, null, ekey);
        }

        if (PickPerson(fields) is { } mine) return (mine, null, ekey);

        // The ownedBy hop. `milestone` carries no person at all, but it declares itself owned by
        // `project` via `project`, and `project.lead` is a person — so "the milestones of the projects
        // I lead" resolves through a link the definition already made, with no new path syntax and no
        // second hop to justify.
        if (via is not null && parentFields is not null && PickPerson(parentFields) is { } theirs)
            return (theirs, via, parentKey);

        errors.Add($"SEMANTIC: entity '{ekey}' is on the calendar but nothing on it references a person"
                 + (parentKey is null
                     ? " — add a person reference (an assignee, an owner), or make it ownedBy an entity that has one"
                     : $", and neither does its parent '{parentKey}' — add a person reference to one of them")
                 + ". An entry nobody is responsible for cannot land in anybody's calendar");
        return (null, null, null);
    }

    private static void ValidateHideWhen(
        string ekey, JsonObject? cfg, Dictionary<string, JsonObject> fields, List<string> errors)
    {
        if (cfg?["hideWhen"] is not JsonObject hw) return;
        foreach (var key in ConditionFields(hw))
            if (!fields.ContainsKey(key))
                errors.Add($"SEMANTIC: entity '{ekey}' has calendar.hideWhen on '{key}', which is not a field on it");
    }

    /// <summary>Every `field` a condition tree reads, `all`/`any` nesting included. `path` leaves are
    /// deliberately not walked — a hop lands on another entity, and this check is about THIS one.</summary>
    private static IEnumerable<string> ConditionFields(JsonObject condition)
    {
        if (Str(condition, "field") is { } f) yield return f;
        foreach (var branch in new[] { "all", "any" })
            if (condition[branch] is JsonArray arr)
                foreach (var child in arr.OfType<JsonObject>())
                    foreach (var nested in ConditionFields(child))
                        yield return nested;
    }

    private static string? PickPerson(Dictionary<string, JsonObject> fields)
    {
        var people = fields.Values.Where(IsPersonRef).Select(f => Str(f, "key")!).ToList();
        if (people.Count == 1) return people[0];
        if (people.Count == 0) return null;
        foreach (var name in WhoPrecedence)
            if (people.Contains(name, StringComparer.Ordinal)) return name;
        // Several people, none of them named anything the precedence knows. Ordinal-first rather than
        // "give up": the alternative is refusing an entity that plainly has a person on it, and the
        // author can always name one explicitly.
        return people.OrderBy(p => p, StringComparer.Ordinal).First();
    }

    private static bool IsSystem(JsonObject f) =>
        f["system"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static bool IsPersonRef(JsonObject f) =>
        Str(f, "type") == "reference" && Str(f, "targetApp") == "platform" && Str(f, "targetEntity") == "person";

    private static string? RoleField(Dictionary<string, JsonObject> fields, string role) =>
        fields.Values.FirstOrDefault(f => Str(f, "role") == role) is { } f ? Str(f, "key") : null;

    private static JsonObject? EntityByKey(JsonArray entities, string key) =>
        entities.OfType<JsonObject>().FirstOrDefault(e => Str(e, "key") == key);

    private static Dictionary<string, JsonObject> FieldsOf(JsonObject entity)
    {
        var map = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var f in (entity["fields"] as JsonArray ?? []).OfType<JsonObject>())
            if (Str(f, "key") is { } k) map[k] = f;
        return map;
    }

    private static string? Str(JsonObject? o, string prop) =>
        o?[prop] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

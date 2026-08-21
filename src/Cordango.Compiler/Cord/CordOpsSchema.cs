// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Definition;

namespace Cordango.Cord;

/// <summary>
/// The domain authoring vocabulary, as provider-neutral JSON Schema.
///
/// <para>Written by hand rather than reflected off the record types, and that is deliberate. The
/// records carry what Cord needs to LOWER an application; this carries what an author needs to
/// DESCRIBE one, and the two are not the same set. Reflection would drag `Via` onto the wire — the one
/// property this whole design exists to take away — along with every property that is only there
/// because the App Definition has it.</para>
///
/// <para><b>Rule 3.</b> Domain only. Behaviour and UI get their own schemas in later slices and this
/// one does not grow to meet them. Its size is asserted against a ceiling with the same doctrine the
/// screen tool schema already carries: <i>if it trips, narrow — do not raise.</i></para>
/// </summary>
internal static class CordOpsSchema
{
    /// <summary>
    /// The schema for ONE operation, by name, with the <c>$defs</c> it needs.
    ///
    /// <para><b>One operation at a time, not the array against the union</b>, and the reason is the
    /// error message. A <c>oneOf</c> of eleven branches where none matches reports every branch's
    /// complaint — "expected \"upsert_entity\"", "required [lifecycle] not present", ten more — and
    /// buries the one real problem under a wall of phantoms. That is the exact failure
    /// <c>Gate.CollectSchemaErrors</c> was written to prune, and it cannot prune it here because no
    /// branch is valid. The operation's NAME is already known by the time this is called, so the right
    /// branch can simply be picked.</para>
    ///
    /// <para>The <c>$defs</c> travel with it: every internal reference is <c>#/$defs/…</c>, which
    /// resolves against the DOCUMENT root, so lifting a subschema out without its definitions would
    /// leave all of them dangling.</para>
    /// </summary>
    public static JsonObject? ForOp(string name)
    {
        foreach (var concern in new[] { Domain(), CordOpsSchemaBehaviour.Behaviour(), CordOpsSchemaUi.Ui() })
        {
            var branch = ((JsonArray)concern["properties"]!["ops"]!["items"]!["oneOf"]!)
                .OfType<JsonObject>()
                .FirstOrDefault(o => (string?)o["title"] == name);
            if (branch is null) continue;

            var schema = (JsonObject)branch.DeepClone();
            if (concern["$defs"] is JsonObject defs) schema["$defs"] = defs.DeepClone();
            return schema;
        }
        return null;
    }

    public static JsonObject Domain() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray("ops"),
        ["properties"] = new JsonObject
        {
            ["note"] = Str("One line on what this change is for. Shown to the person waiting."),
            ["ops"] = new JsonObject
            {
                ["type"] = "array",
                ["minItems"] = 1,
                // The upsert rule is stated ONCE, here, rather than repeated on each operation — see
                // the note in CordOpsSchemaBehaviour.
                ["description"] =
                    "Applied in order. Either they all pass validation or none are kept. Each upsert "
                    + "adds one thing or replaces the one that already has its key, stated in full — "
                    + "anything omitted is cleared — and touches nothing else. Send several in one call.",
                ["items"] = new JsonObject
                {
                    ["oneOf"] = new JsonArray(UpsertEntity(), UpsertField(), Remove()),
                },
            },
        },
        ["$defs"] = new JsonObject
        {
            ["entity"] = Entity(),
            ["field"] = Field(),
            ["aggregate"] = Aggregate(),
        },
    };

    // ---- operations ------------------------------------------------------------------------------

    private static JsonObject UpsertEntity() => Op("upsert_entity",
        "One entity, with its fields. Send one per entity in a single call to establish a whole domain "
        + "at once.",
        new JsonObject { ["entity"] = Ref("#/$defs/entity") }, "entity");

    private static JsonObject UpsertField() => Op("upsert_field",
        "One field on an existing entity, when the entity itself is otherwise unchanged.",
        new JsonObject { ["entity"] = Str("Entity key."), ["field"] = Ref("#/$defs/field") },
        "entity", "field");

    private static JsonObject Remove() => Op("remove",
        "Remove an entity, or a field when `field` is given.",
        new JsonObject
        {
            ["entity"] = Str("Entity key."),
            ["field"] = Str("Field key. Omit to remove the entity itself."),
        },
        "entity");



    private static JsonObject Entity() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray("key", "label", "fields"),
        ["properties"] = new JsonObject
        {
            ["key"] = Str("snake_case identifier, singular."),
            ["label"] = Str("Singular, as a person would say it."),
            ["labelPlural"] = Str("Plural, if it is not just label + s."),
            ["description"] = Str("What one record of this represents."),
            ["icon"] = Str("Material Design Icons name, without the mdi- prefix."),
            ["displayField"] = Str("The field that names a record in pickers and links."),
            ["kind"] = Enum(
                "collection (default): people browse these. config: a lookup list other entities point "
                + "at, filed under Configuration. settings: a single record of app-wide settings.",
                "collection", "config", "settings"),
            ["ownedBy"] = new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["required"] = new JsonArray("parent"),
                ["description"] =
                    "Set when records only exist inside a parent — line items, comments, answers. They "
                    + "get no navigation of their own and appear inside the parent instead. Give this "
                    + "entity a reference field pointing at the parent; which one that is gets worked "
                    + "out for you.",
                ["properties"] = new JsonObject { ["parent"] = Str("Parent entity key.") },
            },
            ["series"] = new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["required"] = new JsonArray("partition", "order"),
                ["description"] =
                    "Set when rows form an ordered run and a figure carries from one to the next — a "
                    + "running balance, a closing stock level. Required before any field can use prev().",
                ["properties"] = new JsonObject
                {
                    ["partition"] = Str("Reference field; rows sharing it form one run."),
                    ["order"] = Str("Numeric or date field giving the order within the run."),
                },
            },
            ["unique"] = new JsonObject
            {
                ["type"] = "array",
                ["description"] = "Field combinations no two records may share. One booking per (room, slot).",
                ["items"] = new JsonObject { ["type"] = "array", ["minItems"] = 2, ["items"] = Str(null) },
            },
            ["fields"] = ArrayOf("#/$defs/field"),
        },
    };

    private static JsonObject Field() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray("key", "label", "type"),
        ["properties"] = new JsonObject
        {
            ["key"] = Str("snake_case identifier."),
            ["label"] = Str("What a person calls it."),
            ["type"] = Enum(null,
                "text", "longtext", "integer", "decimal", "money", "boolean", "date", "datetime",
                "email", "url", "phone", "select", "multiselect", "reference", "json", "attachment"),
            ["required"] = Bool("A record cannot be saved without it."),
            ["unique"] = Bool("No two records may share this value."),
            ["indexed"] = Bool("Set on fields that are filtered or sorted often."),
            ["help"] = Str("A sentence under the input, when the label is not enough."),
            ["group"] = Str("Section heading to file this under on the record."),
            ["default"] = new JsonObject { ["description"] = "Value a new record starts with." },
            ["unit"] = Str("Suffix shown after the number — kg, hours, %. Never on money."),
            ["currency"] = Str("ISO 4217 code for a money field."),
            ["role"] = Enum(
                "status: the one governed state field, which drives boards and steppers. start/due: "
                + "the dates a calendar or timeline reads. order: manual drag ordering.",
                "status", "start", "due", "order"),
            // The platform's entity keys are NAMED, not described. Three of six apps in the first live
            // run spent a whole repair round on `targetEntity: "user"` — a reasonable guess at a closed
            // set the model could not see. "people/departments/groups" told it the concepts and left it
            // to invent the identifiers. Listed from Schemas.PlatformEntityKeys so this cannot drift
            // from what Gate.cs will accept.
            ["target"] = Str(
                "For type=reference: the entity key this points at. With targetApp='platform' it must "
                + $"be one of: {string.Join(", ", Schemas.PlatformEntityKeys.OrderBy(x => x, StringComparer.Ordinal))}. "
                + "There is no 'user' — a human being is 'person'. A company is 'organization'."),
            // Naming 'platform' and stopping there is what caused a real defect: an agent asked to link
            // tasks to organizations read the platform set, found no organization in it, took "that
            // app's key" to mean an app in its own workspace, and declared a second organization
            // entity. The core apps were reachable the whole time. Kept to a LIST, not an explanation —
            // `cord inspect` and `cord vocabulary core <key>` carry the detail, and this string is
            // prefix on every domain call.
            ["targetApp"] = Str(
                "Into another app: 'platform' for the shared "
                + $"{string.Join("/", Schemas.PlatformEntityKeys.OrderBy(x => x, StringComparer.Ordinal))} "
                + $"records, or a core app — {string.Join(", ", CoreAppRegistry.All.Select(a => a.SystemKey))}. "
                + "Never re-declare a core app. Omit within this app."),
            ["onDelete"] = Enum(
                "What happens to this record when the referenced one is deleted.",
                "cascade", "restrict", "setNull"),
            ["options"] = new JsonObject
            {
                ["type"] = "array",
                ["description"] = "For select/multiselect: the choices. Prefer a closed set over free text.",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["required"] = new JsonArray("value", "label"),
                    ["properties"] = new JsonObject
                    {
                        ["value"] = Str("Stored value, snake_case."),
                        ["label"] = Str("Shown to people."),
                        ["color"] = Str("#rrggbb."),
                    },
                },
            },
            ["expr"] = Str(
                "Worked out from this record's own fields, recalculated on every write: "
                + "`gross_salary * (1 + employer_cost_rate / 100)`. Arithmetic, comparisons, and/or/not, "
                + "pow(). On a series entity, `prev(field, seed)` reads the row before this one — that is "
                + "how a running balance is expressed. A field with expr is never typed in."),
            ["aggregate"] = Ref("#/$defs/aggregate"),
        },
    };

    /// <summary>
    /// The aggregate — and what is NOT here is the point.
    ///
    /// <para>No <c>via</c>: which reference joins the two entities is derived. No <c>match</c>: naming
    /// a reference in <c>over</c> rather than saying <c>"mine"</c> already says it. No
    /// <c>against</c>/<c>at</c> split to remember: whichever field belongs to the record being computed
    /// is called <c>myPoint</c> or <c>myFrom</c>/<c>myTo</c>, and the variant name says which direction
    /// the window runs.</para>
    /// </summary>
    private static JsonObject Aggregate() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray("op", "of"),
        ["description"] =
            "Worked out from OTHER records. An invoice's total, a period's payroll cost, a project's "
            + "open task count.",
        ["properties"] = new JsonObject
        {
            ["op"] = Enum(null, "count", "sum", "avg", "min", "max"),
            ["of"] = Str("The entity whose records are added up."),
            ["field"] = Str("The numeric field on it. Omit only for count."),
            ["over"] = Str(
                "How those records relate to this one. \"mine\" for records that point directly at this "
                + "record — an invoice totalling its own lines. Otherwise name a reference field on THIS "
                + "entity, meaning records that point at the same thing it does: a period sums the hiring "
                + "lines of its own scenario, where neither owns the other and they share a parent. "
                + "Defaults to \"mine\"."),
            ["during"] = new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["description"] =
                    "Count a record only while it is in range. Use exactly one direction. Anything named "
                    + "my* is a field on THIS record; everything else is on the records being added up. "
                    + "Bounds may be dates or numbers as long as both sides agree. An omitted end is open.",
                ["properties"] = new JsonObject
                {
                    ["covering"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = false,
                        ["required"] = new JsonArray("from", "myPoint"),
                        ["description"] =
                            "The record spans a RANGE and must cover a point on mine — a hire being paid "
                            + "in this month.",
                        ["properties"] = new JsonObject
                        {
                            ["from"] = Str("Start of the record's range."),
                            ["to"] = Str("End of it. Omit for open-ended."),
                            ["myPoint"] = Str("The date or number on THIS record it must cover."),
                        },
                    },
                    ["inside"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = false,
                        ["required"] = new JsonArray("at", "myFrom"),
                        ["description"] =
                            "The record has ONE date and must fall inside a range on mine — a payment "
                            + "landing in the month it is made.",
                        ["properties"] = new JsonObject
                        {
                            ["at"] = Str("The record's own date or number."),
                            ["myFrom"] = Str("Start of THIS record's range."),
                            ["myTo"] = Str("End of it. Omit for open-ended."),
                        },
                    },
                },
            },
            ["where"] = new JsonObject
            {
                ["type"] = "array",
                ["maxItems"] = 5,
                ["description"] = "Narrow which records count — only the ones whose basis is 'revenue'.",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["required"] = new JsonArray("field", "value"),
                    ["properties"] = new JsonObject
                    {
                        ["field"] = Str("Field on the aggregated entity."),
                        ["operator"] = Enum(null, "eq", "neq", "gt", "gte", "lt", "lte", "in", "notIn"),
                        ["value"] = new JsonObject { ["description"] = "What to compare against." },
                    },
                },
            },
        },
    };

    // ---- helpers ---------------------------------------------------------------------------------

    private static JsonObject Op(string name, string description, JsonObject properties, params string[] required)
    {
        var props = new JsonObject { ["op"] = new JsonObject { ["const"] = name } };
        foreach (var (key, value) in properties.ToList())
        {
            properties.Remove(key);
            props[key] = value;
        }
        return new JsonObject
        {
            ["title"] = name,
            ["description"] = description,
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray(new[] { "op" }.Concat(required).Select(r => (JsonNode)r!).ToArray()),
            ["properties"] = props,
        };
    }

    private static JsonObject Str(string? description)
    {
        var o = new JsonObject { ["type"] = "string" };
        if (description is not null) o["description"] = description;
        return o;
    }

    private static JsonObject Bool(string description) =>
        new() { ["type"] = "boolean", ["description"] = description };

    private static JsonObject Enum(string? description, params string[] values)
    {
        var o = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(values.Select(v => (JsonNode)v!).ToArray()) };
        if (description is not null) o["description"] = description;
        return o;
    }

    private static JsonObject Ref(string pointer) => new() { ["$ref"] = pointer };

    private static JsonObject ArrayOf(string pointer) =>
        new() { ["type"] = "array", ["minItems"] = 1, ["items"] = Ref(pointer) };
}

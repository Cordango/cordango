// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Cord;

/// <summary>
/// The wire shape the model writes → the model Cord holds.
///
/// <para>Deliberately NOT the App Definition's spelling. The differences are the whole point of the
/// layer, and each one removes a decision that was never the author's to make:</para>
///
/// <list type="bullet">
/// <item><c>target</c> rather than <c>targetEntity</c> — shorter, and there is only one thing a
/// reference can target.</item>
/// <item><c>aggregate</c> rather than <c>computed.rollup</c>, with no <c>via</c>, no <c>match</c> and
/// no <c>window.against</c>. Those are derived (<see cref="CordRefIndex"/>) or implied by which record
/// each date belongs to.</item>
/// <item><c>expr</c> sits directly on the field rather than under <c>computed</c> — a field is either
/// typed in or worked out, and nesting that one level deeper only ever cost a submission.</item>
/// </list>
///
/// <para>Anything the wire does not mention is simply absent from the model. There is no raw variant
/// here and there will not be one: reading an unmodelled construct is <see cref="CordImport"/>'s job,
/// writing one is how the semantic surface would get bypassed a convenient exception at a time.</para>
/// </summary>
internal static class CordWire
{
    public static CordEntity Entity(JsonObject o) => new(
        Key: Str(o, "key"),
        Label: Str(o, "label"),
        LabelPlural: Str(o, "labelPlural"),
        Description: Str(o, "description"),
        Icon: Str(o, "icon"),
        DisplayField: Str(o, "displayField"),
        Kind: Str(o, "kind"),
        // `via` is not on the wire at all — which field points at the parent is derived, exactly as a
        // rollup's join is.
        Owner: o["ownedBy"] is JsonObject owned && Str(owned, "parent") is { } parent
            ? new CordOwnership(parent)
            : null,
        Series: o["series"] is JsonObject s && Str(s, "partition") is { } partition && Str(s, "order") is { } order
            ? new CordSeries(partition, order)
            : null,
        Fields: o["fields"] is JsonArray fields ? fields.OfType<JsonObject>().Select(Field).ToList() : null,
        Unique: o["unique"] is JsonArray unique
            ? unique.OfType<JsonArray>()
                .Select(g => (IReadOnlyList<string>)g.OfType<JsonValue>()
                    .Select(v => v.TryGetValue<string>(out var k) ? k : null)
                    .Where(k => k is not null).Select(k => k!).ToList())
                .ToList()
            : null,
        // Nullable, and the three states are distinct: absent leaves it alone, true opts in, false is
        // a deliberate opt-OUT that an edit can use to take an entity back off the calendar.
        Calendar: o["calendar"] is JsonValue cal && cal.TryGetValue<bool>(out var onCal) ? onCal : null);

    public static CordField Field(JsonObject o) => new(
        Key: Str(o, "key"),
        Label: Str(o, "label"),
        Type: Str(o, "type"),
        Required: Bool(o, "required"),
        Unique: Bool(o, "unique"),
        Indexed: Bool(o, "indexed"),
        Help: Str(o, "help"),
        Group: Str(o, "group"),
        Default: o["default"]?.DeepClone(),
        Unit: Str(o, "unit"),
        Currency: Str(o, "currency"),
        Role: Str(o, "role"),
        // `target`, not `targetEntity`.
        TargetEntity: Str(o, "target"),
        TargetApp: Str(o, "targetApp"),
        OnDelete: Str(o, "onDelete"),
        Options: o["options"] is JsonArray options
            ? options.OfType<JsonObject>()
                .Where(x => Str(x, "value") is not null && Str(x, "label") is not null)
                .Select(x => new CordOption(Str(x, "value")!, Str(x, "label")!, Str(x, "color"), Str(x, "phase")))
                .ToList()
            : null,
        Calc: Calc(o));

    private static CordCalc? Calc(JsonObject o)
    {
        if (Str(o, "expr") is { } expr) return new CordExpr(expr);
        if (o["aggregate"] is not JsonObject a) return null;

        var op = Str(a, "op");
        var of = Str(a, "of");
        if (op is null || of is null) return null;

        return new CordAggregate(
            op, of,
            Field: Str(a, "field"),
            Over: Str(a, "over") ?? CordAggregate.Mine,
            // Never on the wire. It is derived, and offering it would hand back the decision the whole
            // aggregate shape exists to take away.
            Via: null,
            During: Window(a["during"] as JsonObject),
            Where: a["where"] is JsonArray w
                ? w.OfType<JsonObject>()
                    .Select(f => new CordFilter(Str(f, "field"), Str(f, "operator") ?? "eq", f["value"]?.DeepClone()))
                    .ToList()
                : null);
    }

    private static CordWindow? Window(JsonObject? during)
    {
        if (during is null) return null;

        if (during["covering"] is JsonObject c && Str(c, "from") is { } from && Str(c, "myPoint") is { } myPoint)
            return new CordCovering(from, Str(c, "to"), myPoint);

        if (during["inside"] is JsonObject i && Str(i, "at") is { } at && Str(i, "myFrom") is { } myFrom)
            return new CordInside(at, myFrom, Str(i, "myTo"));

        return null;
    }

    private static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool? Bool(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
}

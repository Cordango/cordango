// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cordango.Definition;

namespace Cordango.Semantics;

/// <summary>Evaluates the shared condition language (command.when / transition.when / workflow.when)
/// against a record: nested <c>all</c>/<c>any</c> or a <c>{ field, operator, value }</c> leaf.
///
/// A leaf reads the record's own <c>field</c>, or hops ONE relation via <c>path</c>
/// ("room.requires_approval") when the caller supplies a <see cref="RecordHop"/> — the same hop
/// <c>initial</c> rules already had, which is what makes a cross-record guard expressible.
/// Cross-record AGGREGATE conditions need nothing new here: express them over a computed rollup.
///
/// Value tokens (<c>{{actor.id}}</c>/<c>{{currentUser.id}}</c>, <c>{{today}}</c>, <c>{{now}}</c> and
/// their offsets) resolve through <see cref="ExprTokens"/> — the same grammar the gate validates.
/// Pure and deterministic given the clock. A malformed condition (which the gate rejects at author
/// time) evaluates to false rather than throwing.</summary>
public static class ConditionEvaluator
{
    /// <summary>Reads <c>&lt;refField&gt;.&lt;targetField&gt;</c> off the referenced record. Returns
    /// null when the reference is empty, cross-app, or unresolved — the guard simply won't match.</summary>
    public delegate JsonNode? RecordHop(JsonObject record, string refField, string targetField);

    public static bool Evaluate(JsonObject? condition, JsonObject record, string actorId, DateTimeOffset now,
        RecordHop? hop = null)
    {
        if (condition is null) return true;   // no guard = always
        if (condition["all"] is JsonArray all)
            return all.OfType<JsonObject>().All(c => Evaluate(c, record, actorId, now, hop));
        if (condition["any"] is JsonArray any)
            return any.OfType<JsonObject>().Any(c => Evaluate(c, record, actorId, now, hop));

        var op = condition["operator"]?.GetValue<string>();
        if (op is null) return false;
        if (!TryOperand(condition, record, hop, out var actual)) return false;
        var expected = ResolveValue(condition["value"], actorId, now);

        return op switch
        {
            "isEmpty" => IsEmpty(actual),
            "isNotEmpty" => !IsEmpty(actual),
            "eq" => Compare(actual, expected) == 0,
            "neq" => Compare(actual, expected) != 0,
            "gt" => Compare(actual, expected) is { } c and > 0 && c != int.MinValue,
            "gte" => Compare(actual, expected) is { } c and >= 0 && c != int.MinValue,
            "lt" => Compare(actual, expected) is { } c and < 0 && c != int.MinValue,
            "lte" => Compare(actual, expected) is { } c && c <= 0 && c != int.MinValue,
            "contains" => AsString(actual).Contains(AsString(expected), StringComparison.OrdinalIgnoreCase),
            "in" => InList(actual, expected),
            "notIn" => !InList(actual, expected),
            "between" => Between(actual, expected),
            "overlaps" => Overlaps(condition, record, actual, expected),
            _ => false,
        };
    }

    /// <summary>The value a leaf compares: its own <c>field</c>, or a one-hop <c>path</c>. False when
    /// the leaf names neither (or names a path with no hop available), so a guard can never match by
    /// accidentally comparing null against null.</summary>
    private static bool TryOperand(JsonObject leaf, JsonObject record, RecordHop? hop, out JsonNode? value)
    {
        value = null;
        if (leaf["field"]?.GetValue<string>() is { } f) { value = record[f]; return true; }
        if (leaf["path"]?.GetValue<string>() is { } path && hop is not null)
        {
            var dot = path.IndexOf('.');
            if (dot <= 0 || dot == path.Length - 1) return false;
            value = hop(record, path[..dot], path[(dot + 1)..]);
            return true;
        }
        return false;
    }

    private static JsonNode? ResolveValue(JsonNode? value, string actorId, DateTimeOffset now)
    {
        // Array values (in / notIn / between / overlaps) resolve element-wise, so a window is
        // writable as ["{{today}}", "{{today+7}}"].
        if (value is JsonArray arr)
            return new JsonArray(arr.Select(e => ResolveValue(e?.DeepClone(), actorId, now)).ToArray());
        if (value is JsonValue jv && jv.GetValueKind() == JsonValueKind.String
            && ExprTokens.Inner(jv.GetValue<string>()) is { } inner
            && ExprTokens.Resolve(inner, actorId, now) is { } resolved)
            return JsonValue.Create(resolved);
        return value;
    }

    private static bool IsEmpty(JsonNode? n) =>
        n is null || n.GetValueKind() == JsonValueKind.Null
        || (n.GetValueKind() == JsonValueKind.String && n.GetValue<string>().Length == 0)
        || (n is JsonArray a && a.Count == 0);

    private static bool InList(JsonNode? actual, JsonNode? expected) =>
        expected is JsonArray arr && arr.Any(e => Compare(actual, e) == 0);

    /// <summary>Inclusive on both ends: <c>[lo, hi]</c>. "Due in the next 7 days" is
    /// <c>between ["{{today}}", "{{today+7}}"]</c>, and both endpoints count.</summary>
    private static bool Between(JsonNode? actual, JsonNode? expected)
    {
        if (expected is not JsonArray { Count: 2 } r || IsEmpty(actual)) return false;
        var lo = Compare(actual, r[0]);
        var hi = Compare(actual, r[1]);
        if (lo is null or int.MinValue || hi is null or int.MinValue) return false;
        return lo >= 0 && hi <= 0;
    }

    /// <summary>Does the record's own range <c>[field, endField]</c> overlap the window
    /// <c>[from, to]</c>?
    ///
    /// <b>Boundary rule:</b> DATE-only endpoints are inclusive — a date names a whole day, so a task
    /// ending Monday does overlap a window starting Monday. Anything carrying a time is half-open —
    /// a booking ending at 10:00 does NOT collide with one starting at 10:00, which is the entire
    /// point of a conflict check. The renderer applies the identical rule.</summary>
    private static bool Overlaps(JsonObject leaf, JsonObject record, JsonNode? start, JsonNode? expected)
    {
        if (expected is not JsonArray { Count: 2 } w) return false;
        if (leaf["endField"]?.GetValue<string>() is not { } endField) return false;
        var end = record[endField];
        if (IsEmpty(start) || IsEmpty(end) || IsEmpty(w[0]) || IsEmpty(w[1])) return false;

        var startVsTo = Compare(start, w[1]);
        var endVsFrom = Compare(end, w[0]);
        if (startVsTo is null or int.MinValue || endVsFrom is null or int.MinValue) return false;

        return DateOnly(start) && DateOnly(end) && DateOnly(w[0]) && DateOnly(w[1])
            ? startVsTo <= 0 && endVsFrom >= 0
            : startVsTo < 0 && endVsFrom > 0;
    }

    /// <summary>A bare calendar date (<c>yyyy-MM-dd</c>) rather than an instant.</summary>
    private static bool DateOnly(JsonNode? n) =>
        n?.GetValueKind() == JsonValueKind.String
        && n.GetValue<string>() is { Length: 10 } s
        && DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    // Returns 0 equal, <0 actual<expected, >0 actual>expected, or int.MinValue when not comparable.
    private static int? Compare(JsonNode? a, JsonNode? b)
    {
        if (IsEmpty(a) || IsEmpty(b)) return IsEmpty(a) && IsEmpty(b) ? 0 : int.MinValue;
        var an = AsNumber(a); var bn = AsNumber(b);
        if (an is not null && bn is not null) return an.Value.CompareTo(bn.Value);
        return string.Compare(AsString(a), AsString(b), StringComparison.Ordinal);
    }

    // Via ToJsonString rather than GetValue<double>: a number node fresh from the engine's own
    // recompute is BACKED by a decimal (not parsed JSON), and GetValue<double> throws on that
    // instead of converting — so a numeric guard over a just-computed total crashed the dispatch.
    // The invariant JSON text round-trips every backing type.
    private static double? AsNumber(JsonNode? n) =>
        n?.GetValueKind() == JsonValueKind.Number && double.TryParse(n.ToJsonString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d
        : n?.GetValueKind() == JsonValueKind.String && double.TryParse(n.GetValue<string>(), NumberStyles.Any, CultureInfo.InvariantCulture, out var s) ? s
        : null;

    private static string AsString(JsonNode? n) =>
        n is null || n.GetValueKind() == JsonValueKind.Null ? ""
        : n.GetValueKind() == JsonValueKind.String ? n.GetValue<string>()
        : n.ToJsonString();
}

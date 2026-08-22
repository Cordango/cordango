// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cordango.Standalone.Conditions;

/// <summary>
/// Does this record satisfy this condition?
///
/// <para>Used by a command's guard, a workflow's <c>when</c>, and a rollup's filters. One
/// implementation, because they are one language.</para>
///
/// <para><b>This is the standalone's OWN evaluator, not the platform's.</b> Sharing the code would
/// mean a change made for a generated application could alter what the platform enforces, which is a
/// blast radius nobody wants for the layer that decides whether an approval is allowed. The two are
/// held together by <c>tests/fixtures/conditions/*.json</c> instead — a list of
/// (record, condition) → expected answers that BOTH suites run. Drift becomes a red test rather than
/// a wrong answer in production, and that is a better guarantee than shared code, because shared
/// code has no test at all.</para>
///
/// <para><b>Nothing throws.</b> A malformed condition is false. The gate rejects one at author time,
/// so reaching here with a broken condition means something upstream failed — and the safe reading
/// of "I do not understand this guard" is that the guarded thing does not happen.</para>
/// </summary>
public static class ConditionEvaluator
{
    /// <summary>Reads <c>&lt;refField&gt;.&lt;targetField&gt;</c> off a referenced record. Null when
    /// the reference is empty or unresolved, in which case the leaf simply does not match.</summary>
    public delegate JsonNode? RecordHop(JsonObject record, string referenceField, string targetField);

    /// <summary>
    /// True when the record satisfies the condition.
    /// </summary>
    /// <param name="condition">Null means "no condition", which is true — an unguarded command runs.</param>
    /// <param name="record">The record, as JSON. Its own values, typed as the database holds them.</param>
    /// <param name="actorId">Who is asking, for <c>{{actor.id}}</c>.</param>
    /// <param name="now">The clock, for <c>{{today}}</c> and its offsets.</param>
    /// <param name="hop">How to follow a <c>path</c> into a referenced record. Without it, a leaf
    /// that names a path is false rather than ignored.</param>
    public static bool Evaluate(
        Condition? condition,
        JsonObject record,
        string? actorId = null,
        DateTimeOffset now = default,
        RecordHop? hop = null)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (condition is null) return true;

        if (condition.All is { Count: > 0 } all)
            return all.All(child => Evaluate(child, record, actorId, now, hop));

        if (condition.Any is { Count: > 0 } any)
            return any.Any(child => Evaluate(child, record, actorId, now, hop));

        if (condition.Not is { } not)
            return !Evaluate(not, record, actorId, now, hop);

        if (condition.Operator is not { } op) return false;
        if (!TryOperand(condition, record, hop, out var actual)) return false;

        var expected = ValueTokens.Resolve(condition.Value, actorId, null, now);

        return op switch
        {
            "isEmpty" => IsEmpty(actual),
            "isNotEmpty" => !IsEmpty(actual),
            "eq" => Compare(actual, expected) == 0,
            "neq" => Compare(actual, expected) != 0,
            "gt" => Compare(actual, expected) is { } c and > 0 && c != Incomparable,
            "gte" => Compare(actual, expected) is { } c and >= 0 && c != Incomparable,
            "lt" => Compare(actual, expected) is { } c and < 0 && c != Incomparable,
            "lte" => Compare(actual, expected) is { } c && c <= 0 && c != Incomparable,
            "contains" => AsString(actual).Contains(expected ?? "", StringComparison.OrdinalIgnoreCase),
            "in" => InList(actual, condition, actorId, now),
            // A blank is NOT in the list, so `notIn` is true for a record nobody has filled in.
            // Debatable, and settled: the platform answers the same, and one of the two had to
            // be the definition of the operator.
            "notIn" => !InList(actual, condition, actorId, now),
            "between" => Between(actual, condition, actorId, now),
            "overlaps" => Overlaps(condition, record, actual, actorId, now),
            _ => false,
        };
    }

    /// <summary>What the leaf reads: the record's own field, or one hop through a reference. False
    /// when it names neither, so a condition can never match by comparing nothing against nothing.</summary>
    private static bool TryOperand(Condition leaf, JsonObject record, RecordHop? hop, out JsonNode? value)
    {
        value = null;

        if (leaf.Field is { Length: > 0 } field)
        {
            value = record[field];
            return true;
        }

        if (leaf.Path is { Length: > 0 } path && hop is not null)
        {
            var dot = path.IndexOf('.', StringComparison.Ordinal);
            if (dot <= 0 || dot == path.Length - 1) return false;
            value = hop(record, path[..dot], path[(dot + 1)..]);
            return true;
        }

        return false;
    }

    private static IReadOnlyList<string?> Expected(Condition leaf, string? actorId, DateTimeOffset now) =>
        [.. (leaf.Values ?? []).Select(v => ValueTokens.Resolve(v, actorId, null, now))];

    private static bool InList(JsonNode? actual, Condition leaf, string? actorId, DateTimeOffset now) =>
        Expected(leaf, actorId, now).Any(e => Compare(actual, e) == 0);

    /// <summary>Inclusive on both ends. "Due in the next 7 days" is
    /// <c>between ["{{today}}", "{{today+7}}"]</c>, and both endpoints count.</summary>
    private static bool Between(JsonNode? actual, Condition leaf, string? actorId, DateTimeOffset now)
    {
        var range = Expected(leaf, actorId, now);
        if (range.Count != 2 || IsEmpty(actual)) return false;

        var low = Compare(actual, range[0]);
        var high = Compare(actual, range[1]);
        if (low is null or Incomparable || high is null or Incomparable) return false;

        return low >= 0 && high <= 0;
    }

    /// <summary>
    /// Does the record's own range <c>[field, endField]</c> overlap the window it is given?
    ///
    /// <para><b>The boundary rule depends on whether the endpoints carry a time.</b> A bare date names
    /// a whole day, so a task ending Monday DOES overlap a window starting Monday. Anything with a
    /// time is half-open: a booking ending at 10:00 does NOT collide with one starting at 10:00,
    /// which is the entire point of a conflict check. The front end applies the identical rule.</para>
    /// </summary>
    private static bool Overlaps(Condition leaf, JsonObject record, JsonNode? start, string? actorId, DateTimeOffset now)
    {
        var window = Expected(leaf, actorId, now);
        if (window.Count != 2) return false;
        if (leaf.EndField is not { Length: > 0 } endField) return false;

        var end = record[endField];
        if (IsEmpty(start) || IsEmpty(end) || window[0] is null or "" || window[1] is null or "") return false;

        var startVersusTo = Compare(start, window[1]);
        var endVersusFrom = Compare(end, window[0]);
        if (startVersusTo is null or Incomparable || endVersusFrom is null or Incomparable) return false;

        return DateOnlyValue(AsString(start)) && DateOnlyValue(AsString(end))
            && DateOnlyValue(window[0]!) && DateOnlyValue(window[1]!)
                ? startVersusTo <= 0 && endVersusFrom >= 0
                : startVersusTo < 0 && endVersusFrom > 0;
    }

    /// <summary>A bare calendar date rather than an instant.</summary>
    private static bool DateOnlyValue(string value) =>
        value is { Length: 10 }
        && DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    /// <summary>Not a real ordering — the answer to "compare these two" when they cannot be. Every
    /// ordered operator checks for it, so <c>gt</c> against a blank is false rather than true by
    /// accident.</summary>
    private const int Incomparable = int.MinValue;

    private static int? Compare(JsonNode? actual, string? expected)
    {
        var actualEmpty = IsEmpty(actual);
        var expectedEmpty = string.IsNullOrEmpty(expected);

        if (actualEmpty || expectedEmpty) return actualEmpty && expectedEmpty ? 0 : Incomparable;

        var a = AsNumber(actual);
        var b = AsNumber(expected);
        if (a is not null && b is not null) return a.Value.CompareTo(b.Value);

        return string.Compare(AsString(actual), expected, StringComparison.Ordinal);
    }

    private static bool IsEmpty(JsonNode? node) =>
        node is null
        || node.GetValueKind() == JsonValueKind.Null
        || (node.GetValueKind() == JsonValueKind.String && node.GetValue<string>().Length == 0)
        || (node is JsonArray array && array.Count == 0);

    /// <summary>
    /// A node as a number, whatever it is backed by.
    ///
    /// <para>Read through <c>ToJsonString</c> rather than <c>GetValue&lt;double&gt;</c>. A number
    /// node that came from a freshly computed field is backed by a <c>decimal</c>, not by parsed
    /// JSON, and <c>GetValue&lt;double&gt;</c> THROWS on that instead of converting — so a numeric
    /// guard over a just-recomputed total would crash rather than answer. The invariant JSON text
    /// round-trips every backing type.</para>
    /// </summary>
    private static double? AsNumber(JsonNode? node) => node?.GetValueKind() switch
    {
        JsonValueKind.Number =>
            double.TryParse(node.ToJsonString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var n) ? n : null,
        JsonValueKind.String =>
            double.TryParse(node.GetValue<string>(), NumberStyles.Any, CultureInfo.InvariantCulture, out var s) ? s : null,
        _ => null,
    };

    private static double? AsNumber(string value) =>
        double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static string AsString(JsonNode? node) =>
        node is null || node.GetValueKind() == JsonValueKind.Null ? ""
        : node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>()
        : node.ToJsonString();
}

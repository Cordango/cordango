// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cordango.Definition;

namespace Cordango.Semantics;

/// <summary>Values a template or condition resolves against: the acting user + the app name (the
/// record is passed separately). now/today are computed at resolution time.</summary>
///
/// <remarks>It lives beside the resolver rather than beside the effect engine that used to declare
/// it, because it is the resolver's INPUT and travels wherever template resolution travels — a
/// generated standalone application resolves templates and has no effect engine to borrow a record
/// from.</remarks>
public sealed record TemplateContext(string ActorId, string? ActorName, string AppName);

/// <summary>Resolves the tiny template grammar used in effect strings:
/// <c>{{record.&lt;field&gt;}}</c>, <c>{{actor.id}}</c>, <c>{{actor.name}}</c>, <c>{{app.name}}</c>,
/// <c>{{today}}</c>, <c>{{now}}</c> — plus everything <see cref="ExprTokens"/> knows (the
/// <c>currentUser.id</c> spelling, date offsets like <c>{{today+7}}</c>), so a token can never pass
/// the gate and then resolve to nothing: the gate validates against that same grammar. Unknown
/// tokens (which the gate rejects at author time) resolve to empty so a stray token never emits
/// literal braces at runtime. Pure and deterministic given a clock.</summary>
public static partial class TemplateResolver
{
    [GeneratedRegex(@"\{\{\s*([^}]+?)\s*\}\}")]
    private static partial Regex TokenRx();

    public static string Resolve(string template, JsonObject record, TemplateContext tc,
        DateTimeOffset now, JsonObject? source = null) =>
        TokenRx().Replace(template, m => TokenValue(m.Groups[1].Value.Trim(), record, tc, now, source) ?? "");

    /// <summary>Resolve a set-value: a bare token string (e.g. "{{record.id}}") yields the raw field
    /// value (preserving type); a string with embedded tokens is rendered; a non-string passes through.</summary>
    /// <param name="source">The row being iterated by a <c>createForEach</c>, addressed as
    /// <c>{{source.field}}</c>. Null for every other effect, where there is no such thing.
    /// <para>Deliberately a SNAPSHOT: the value is copied into the new record and never tracked. A
    /// payslip must not change when that employee's salary changes next year, and an invoice line must
    /// not reprice retroactively — which is exactly the opposite of what a hop or a rollup does.</para></param>
    public static JsonNode? ResolveValue(JsonNode? value, JsonObject record, TemplateContext tc,
        DateTimeOffset now, JsonObject? source = null)
    {
        if (value is not JsonValue jv || jv.GetValueKind() != JsonValueKind.String) return value?.DeepClone();
        var s = jv.GetValue<string>();
        var whole = TokenRx().Match(s);
        // A value that is EXACTLY one token → return the underlying value with its real type.
        if (whole.Success && whole.Value.Length == s.Length && whole.Index == 0)
        {
            var token = whole.Groups[1].Value.Trim();
            if (token.StartsWith("record.", StringComparison.Ordinal))
                return record[token["record.".Length..]]?.DeepClone();
            if (source is not null && token.StartsWith("source.", StringComparison.Ordinal))
                return source[token["source.".Length..]]?.DeepClone();
            return JsonValue.Create(TokenValue(token, record, tc, now, source) ?? "");
        }
        return JsonValue.Create(Resolve(s, record, tc, now, source));
    }

    private static string? TokenValue(string token, JsonObject record, TemplateContext tc,
        DateTimeOffset now, JsonObject? source = null)
    {
        if (token.StartsWith("record.", StringComparison.Ordinal))
        {
            var node = record[token["record.".Length..]];
            return node is null || node.GetValueKind() == JsonValueKind.Null ? "" : Scalar(node);
        }
        if (source is not null && token.StartsWith("source.", StringComparison.Ordinal))
        {
            var node = source[token["source.".Length..]];
            return node is null || node.GetValueKind() == JsonValueKind.Null ? "" : Scalar(node);
        }
        return token switch
        {
            "actor.id" => tc.ActorId,
            "actor.name" => tc.ActorName ?? tc.ActorId,
            "app.name" => tc.AppName,
            // "today"/"now" stay explicit for byte-format compatibility: ExprTokens normalizes
            // "now" to UTC, while this has always rendered the clock's own offset.
            "today" => now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "now" => now.ToString("o", CultureInfo.InvariantCulture),
            // The shared grammar the gate validates against — currentUser.id and the date-offset
            // tokens. Without this they passed the gate and silently resolved to "" here.
            _ => ExprTokens.Resolve(token, tc.ActorId, now) ?? "",
        };
    }

    private static string Scalar(JsonNode node) =>
        node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : node.ToJsonString();
}

// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cordango.Standalone.Security;

/// <summary>
/// What a caller may SEE of a record — the one derivation of that question, for every path that
/// hands data back.
///
/// <para><b>Why it is one function and not a habit.</b> A field hidden from a role has to stay
/// hidden on every route that can carry it, and there are more of those than there look to be: the
/// record itself, the record a command hands back after running, and any message rendered from the
/// record — a <c>successMessage</c> containing <c>{{salary}}</c> leaks the salary in prose just as
/// surely as the field would. Stripping at each call site means the next route added inherits
/// nothing.</para>
///
/// <para>These are the CALLER-facing projections only. Workflows and effects run against the whole
/// record: a notification email may legitimately quote a field its recipient could not have asked
/// for, because it goes to a configured destination rather than back down the wire to whoever
/// happened to make the request.</para>
/// </summary>
public static class RecordVisibility
{
    /// <summary>True when this caller is hiding anything at all — the cheap test that lets
    /// administrators and unrestricted roles skip the work entirely.</summary>
    public static bool Restricts(EntityAccess access)
    {
        ArgumentNullException.ThrowIfNull(access);
        return access.HiddenReadFields.Any();
    }

    /// <summary>
    /// The record as this caller may see it.
    ///
    /// <para>Returns a new object rather than editing the one it was given. The caller is usually
    /// holding an entity the change tracker also holds, and quietly emptying its fields on the way
    /// out would corrupt the very rows a workflow is about to write.</para>
    /// </summary>
    public static JsonObject? Project(EntityAccess access, JsonObject? record)
    {
        ArgumentNullException.ThrowIfNull(access);
        if (record is null) return null;

        var hidden = access.HiddenReadFields.ToHashSet(StringComparer.Ordinal);
        if (hidden.Count == 0) return record;

        var clone = (JsonObject)record.DeepClone();
        foreach (var field in hidden) clone.Remove(field);
        return clone;
    }

    /// <summary>Serialise an entity and project it in one step — what the controllers use.</summary>
    public static JsonObject? Project<T>(EntityAccess access, T? record, JsonSerializerOptions options)
    {
        if (record is null) return null;
        var node = JsonSerializer.SerializeToNode(record, options) as JsonObject;
        return Project(access, node);
    }

    /// <summary>Every field of a write the caller was not allowed to set. Returned rather than
    /// thrown so the caller can name all of them at once: telling somebody about one rejected field
    /// at a time, over four round trips, is a worse answer than one list.</summary>
    public static IReadOnlyList<string> RejectedWrites(EntityAccess access, IEnumerable<string> suppliedFields)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(suppliedFields);

        var restricted = access.WriteRestrictedFields.ToHashSet(StringComparer.Ordinal);
        if (restricted.Count == 0) return [];

        return [.. suppliedFields.Where(restricted.Contains).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }
}

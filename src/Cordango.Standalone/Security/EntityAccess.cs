// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace Cordango.Standalone.Security;

/// <summary>How one field may be touched: <c>Read</c> = it appears in responses, <c>Update</c> = it
/// may be set on create and update.</summary>
public sealed record FieldRule(bool Read, bool Update);

/// <summary>
/// One caller's effective access to one entity: the union of every role they hold, resolved down to
/// entity-level CRUD plus the fields that differ from it.
///
/// <para>Immutable and computed from data alone, so it can be built once per request and asked
/// anything without touching the database or the HTTP context.</para>
/// </summary>
public sealed class EntityAccess
{
    private readonly IReadOnlyDictionary<string, FieldRule> _fields;
    private readonly IReadOnlySet<string> _commands;
    private readonly bool _allCommands;

    public EntityAccess(
        bool create, bool read, bool update, bool delete,
        IReadOnlyDictionary<string, FieldRule>? fields = null,
        IReadOnlySet<string>? commands = null,
        bool allCommands = false)
    {
        Create = create;
        Read = read;
        Update = update;
        Delete = delete;
        _fields = fields ?? EmptyFields;
        _commands = commands ?? EmptyCommands;
        _allCommands = allCommands;
    }

    public bool Create { get; }
    public bool Read { get; }
    public bool Update { get; }
    public bool Delete { get; }

    private static readonly IReadOnlyDictionary<string, FieldRule> EmptyFields =
        new Dictionary<string, FieldRule>(StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> EmptyCommands = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>An administrator: everything, including every command.</summary>
    public static readonly EntityAccess Full = new(true, true, true, true, allCommands: true);

    /// <summary>Nothing at all — no role of the caller's says anything about this entity.</summary>
    public static readonly EntityAccess None = new(false, false, false, false);

    /// <summary>What a caller gets on an application whose definition declares no roles: they may
    /// look, and that is all. An application that has decided nothing about access should not have
    /// write access decided for it.</summary>
    public static readonly EntityAccess ReadOnly = new(false, true, false, false);

    public bool Allows(string operation) => operation switch
    {
        "create" => Create,
        "read" => Read,
        "update" => Update,
        "delete" => Delete,
        _ => false,
    };

    /// <summary>A field with no override inherits the entity-level answer.</summary>
    public bool CanReadField(string field) => _fields.TryGetValue(field, out var rule) ? rule.Read : Read;

    public bool CanUpdateField(string field) => _fields.TryGetValue(field, out var rule) ? rule.Update : Update;

    /// <summary>Fields to strip from responses.</summary>
    public IEnumerable<string> HiddenReadFields => _fields.Where(kv => !kv.Value.Read).Select(kv => kv.Key);

    /// <summary>Fields a write may not touch. Keyed off explicit overrides only, so a role that may
    /// create records can still set every ordinary field — the restriction is the exception, not the
    /// default.</summary>
    public IEnumerable<string> WriteRestrictedFields => _fields.Where(kv => !kv.Value.Update).Select(kv => kv.Key);

    /// <summary>Deny-by-default. True only for an administrator, or when some grant of the caller's
    /// names this command (or <c>"*"</c>).</summary>
    public bool CanRunCommand(string commandKey) => _allCommands || _commands.Contains(commandKey);

    /// <summary>The command keys allowed, for the client's own enablement decisions. <c>"*"</c> when
    /// every command is allowed.</summary>
    public IReadOnlyCollection<string> AllowedCommands => _allCommands ? ["*"] : [.. _commands];
}

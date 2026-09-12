// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.SourceGen.Common;

namespace Cordango.SourceGen.Node.Emit;

/// <summary>
/// The definition's roles, compiled into the application as data.
///
/// <para><b>Data rather than code, and that is the whole point.</b> A permission rule expressed as
/// an <c>if</c> somewhere is a rule that can be forgotten at the second call site. This table is
/// read by ONE resolver, which every read and every write goes through — so a role's grant applies
/// to a browser, a script and an AI client identically, and adding a new way into the application
/// cannot add a new way around the rules.</para>
///
/// <para>Four rules decide an answer, and they are the runtime's, not this emitter's: any role the
/// caller holds that allows an operation allows it; within one role a grant naming the entity
/// replaces that role's <c>*</c> grant; a field's answer is that role's override if it has one, else
/// that role's entity default, and only then unioned across roles; and commands are denied unless a
/// grant names them.</para>
/// </summary>
public static class PermissionsEmitter
{
    public static GeneratedFile Emit(AppModel app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var source = new TsSource();

        source.Lines(Header.For(
            $"Who may do what in {app.Name}.",
            "The definition's roles, as the table the permission resolver reads. Every read and every\n"
            + "write in this application resolves through it — including the ones that do not come from a\n"
            + "browser — so there is one answer to \"may they?\" rather than one per entry point.\n"
            + "\n"
            + "The Administrator role is NOT in this table and cannot be: it is the runtime's own bypass,\n"
            + "and an application whose definition could grant it would be one whose author can promote\n"
            + "themselves by editing a file."));

        source.Line();
        source.Line("import type { AppPermissions } from \"@cordango/standalone\";");
        source.Line();

        if (app.Roles.Count == 0)
        {
            source.Lines(TypeScript.Doc(
                "This definition declares no roles. The resolver answers read-only for anybody signed "
                + "in and nothing at all for anybody who is not — rather than inventing a permission "
                + "model for an application that decided not to have one.", 0));
            source.Line("export const appPermissions: AppPermissions = { roles: [] };");
            return new GeneratedFile("api/src/permissions.ts", source.ToString());
        }

        source.Line("export const appPermissions: AppPermissions = {");
        source.Indent();
        source.Line("roles: [");
        source.Indent();

        foreach (var role in app.Roles) EmitRole(source, role);

        source.Outdent();
        source.Line("],");
        source.Outdent();
        source.Line("};");

        return new GeneratedFile("api/src/permissions.ts", source.ToString());
    }

    private static void EmitRole(TsSource source, JsonObject role)
    {
        var key = AppModel.Str(role["key"]) ?? "role";
        var label = AppModel.Str(role["label"]);

        if (label is { Length: > 0 } && !string.Equals(label, key, StringComparison.Ordinal))
            source.Line($"// {label}");

        source.Line("{");
        source.Indent();
        source.Line($"key: {TypeScript.Literal(key)},");
        source.Line("grants: [");
        source.Indent();

        foreach (var grant in AppModel.Arr(role["grants"]).OfType<JsonObject>()) EmitGrant(source, grant);

        source.Outdent();
        source.Line("],");
        source.Outdent();
        source.Line("},");
    }

    /// <summary>
    /// One grant.
    ///
    /// <para>An operation the grant does not allow is written as <c>false</c> rather than left out.
    /// The compiler has already resolved every grant into an explicit four-way answer, so an absent
    /// flag here would be a second, quieter way of saying the same thing — and the resolver reads
    /// absence as "this grant is silent", which is a different rule.</para>
    /// </summary>
    private static void EmitGrant(TsSource source, JsonObject grant)
    {
        var entity = AppModel.Str(grant["entity"]) ?? "*";

        var overrides = AppModel.Arr(grant["fieldOverrides"]).OfType<JsonObject>().ToList();
        var commands = AppModel.Arr(grant["commands"])
            .Select(c => AppModel.Str(c))
            .Where(c => c is not null)
            .Select(c => c!)
            .ToList();

        var flags = new[] { "create", "read", "update", "delete" }
            .Select(operation => $"{operation}: {TypeScript.Bool(AppModel.Bool(grant[operation]))}");

        var head = $"entity: {TypeScript.Literal(entity)}, " + string.Join(", ", flags);

        if (overrides.Count == 0 && commands.Count == 0)
        {
            source.Line($"{{ {head} }},");
            return;
        }

        source.Line("{");
        source.Indent();
        source.Line(head + ",");

        if (commands.Count > 0)
            source.Line($"commands: [{string.Join(", ", commands.Select(TypeScript.Literal))}],");

        if (overrides.Count > 0)
        {
            source.Line("fieldOverrides: [");
            source.Indent();

            foreach (var rule in overrides)
            {
                var field = AppModel.Str(rule["field"]);
                if (field is null) continue;

                var parts = new List<string> { $"field: {TypeScript.Literal(field)}" };

                // An override that says nothing about one side falls through to the role's
                // entity-level answer. Writing `false` where the definition said nothing would be a
                // restriction nobody asked for, applied to every field the override touches.
                foreach (var side in new[] { "read", "update" })
                    if (rule[side] is JsonValue value && value.TryGetValue<bool>(out var allowed))
                        parts.Add($"{side}: {TypeScript.Bool(allowed)}");

                source.Line($"{{ {string.Join(", ", parts)} }},");
            }

            source.Outdent();
            source.Line("],");
        }

        source.Outdent();
        source.Line("},");
    }
}

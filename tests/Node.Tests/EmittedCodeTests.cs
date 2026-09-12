// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace Cordango.Node.Tests;

/// <summary>What the definition actually becomes, read out of the emitted TypeScript.</summary>
public class EmittedCodeTests
{
    /// <summary>An entity becomes an interface and a descriptor, and a field keeps its key in
    /// both — there is no mapping layer between what the definition called a field, what the column
    /// is called and what a developer types.</summary>
    [Fact]
    public void An_entity_becomes_an_interface_and_a_descriptor()
    {
        var entities = Build.Content(Build.Generate("expenses"), "api/src/entities.ts");

        Assert.Contains("export interface ExpenseClaim extends RecordRow {", entities, StringComparison.Ordinal);
        Assert.Contains("export const expenseClaimDescriptor = new RecordDescriptor<ExpenseClaim>(",
            entities, StringComparison.Ordinal);
        Assert.Contains("\"expense_claim\",", entities, StringComparison.Ordinal);
        Assert.Contains("{ key: \"amount\", type: \"money\" },", entities, StringComparison.Ordinal);
    }

    /// <summary>
    /// Money is a decimal, never a number.
    ///
    /// <para>A JavaScript number cannot hold 0.1 + 0.2, and an invoice total is exactly the value
    /// nobody may round for you. This is the single most consequential type decision the emitter
    /// makes, so it is asserted rather than assumed.</para>
    /// </summary>
    [Fact]
    public void Money_and_decimals_are_Dec()
    {
        var entities = Build.Content(Build.Generate("expenses"), "api/src/entities.ts");

        Assert.Contains("amount: Dec;", entities, StringComparison.Ordinal);
        Assert.DoesNotContain("amount: number", entities, StringComparison.Ordinal);
    }

    /// <summary>An optional field is nullable, because a definition's optional field has no value
    /// until somebody enters one — and representing "not entered" as zero or as an empty string
    /// would make a sum wrong and a filter lie.</summary>
    [Fact]
    public void An_optional_field_is_nullable_and_a_required_one_is_not()
    {
        var entities = Build.Content(Build.Generate("expenses"), "api/src/entities.ts");

        Assert.Contains("spent_on: PlainDate;", entities, StringComparison.Ordinal);
        Assert.Contains("reimbursed_on: PlainDate | null;", entities, StringComparison.Ordinal);
    }

    /// <summary>The audit columns are readable and are not in the descriptor: a client that could
    /// name `created_by` in a payload could claim somebody else wrote the row.</summary>
    [Fact]
    public void The_audit_columns_are_readable_but_not_writable()
    {
        var entities = Build.Content(Build.Generate("expenses"), "api/src/entities.ts");

        Assert.Contains("created_at?: Instant | null;", entities, StringComparison.Ordinal);
        Assert.DoesNotContain("{ key: \"created_by\"", entities, StringComparison.Ordinal);
    }

    /// <summary>A command carries its own rules — which states it may run from, what it needs, what
    /// it writes — because the runtime enforces them from this table rather than from a route
    /// somebody wrote a second copy of them into.</summary>
    [Fact]
    public void A_command_carries_its_transition_and_its_required_input()
    {
        var commands = Build.Content(Build.Generate("expenses"), "api/src/commands.ts");

        Assert.Contains("key: \"approve_claim\",", commands, StringComparison.Ordinal);
        Assert.Contains("stateField: \"status\",", commands, StringComparison.Ordinal);
        Assert.Contains("fromStates: [\"submitted\"],", commands, StringComparison.Ordinal);
        Assert.Contains("toState: \"approved\",", commands, StringComparison.Ordinal);
        Assert.Contains("requiredInputFields: [\"decline_reason\"],", commands, StringComparison.Ordinal);
    }

    /// <summary>
    /// A guard travels as DATA, and every guard the language can express is enforced.
    ///
    /// <para>The .NET target has to compile a condition into C# and report the ones it cannot write,
    /// because a command generated WITHOUT its guard would run in cases the definition refuses. Here
    /// the runtime carries the same evaluator the platform does, so the condition travels as the
    /// object the definition wrote and there is nothing to lose in translation.</para>
    /// </summary>
    [Fact]
    public void A_guard_is_emitted_as_the_condition_the_definition_wrote()
    {
        var withGuards = Build.Corpus()
            .Select(key => (Key: key, Source: Build.Content(Build.Generate(key), "api/src/commands.ts")))
            .Where(x => x.Source.Contains("when: {", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(withGuards);

        // And no application anywhere reports a guard it could not write, because none can exist:
        // the condition travels as data, so there is no translation step to fail. CORD2306 is the
        // code the .NET target raises when its condition emitter cannot express one.
        foreach (var key in Build.Corpus())
            Assert.DoesNotContain(Build.Generate(key).Warnings, w => w.Code == "CORD2306");
    }

    /// <summary>The roles become the table the resolver reads, deny-by-default throughout.</summary>
    [Fact]
    public void The_roles_become_a_permission_table()
    {
        var permissions = Build.Content(Build.Generate("expenses"), "api/src/permissions.ts");

        Assert.Contains("export const appPermissions: AppPermissions = {", permissions, StringComparison.Ordinal);
        Assert.Contains("key: \"employee\",", permissions, StringComparison.Ordinal);
        Assert.Contains("entity: \"expense_claim\"", permissions, StringComparison.Ordinal);

        // The administrator is the runtime's own bypass and cannot be one of the definition's roles.
        Assert.DoesNotContain("key: \"Administrator\"", permissions, StringComparison.Ordinal);
    }

    /// <summary>Every entity gets a route, and the table is one readable line per entity rather
    /// than a loop that saves typing.</summary>
    [Fact]
    public void Every_entity_gets_a_route()
    {
        var routes = Build.Content(Build.Generate("expenses"), "api/src/routes.ts");

        Assert.Contains("api.use(\"/expense_claim\", recordsRouter(\"expense_claim\"));",
            routes, StringComparison.Ordinal);
    }

    /// <summary>The directory is registered before the application's own entities, so a reference
    /// from one of them has something to resolve against.</summary>
    [Fact]
    public void The_directory_is_registered_first()
    {
        var app = Build.Content(Build.Generate("expenses"), "api/src/app.ts");

        var directory = app.IndexOf("addDirectory(runtime);", StringComparison.Ordinal);
        var first = app.IndexOf("runtime.register<", StringComparison.Ordinal);

        Assert.True(directory >= 0 && first > directory,
            "The directory must be registered before the application's own entities.");
    }

    /// <summary>
    /// A field the runtime owns is filled by a hook rather than left to the form.
    ///
    /// <para>They are hidden from every form, so if nothing filled them they would simply be null —
    /// and "who submitted this" being null on every row is the sort of emptiness that looks like a
    /// data problem rather than a missing feature.</para>
    /// </summary>
    [Fact]
    public void An_automatic_field_is_filled_on_the_way_in()
    {
        var result = Build.Generate("expenses");
        var auto = Build.Content(result, "api/src/auto/expense_claim.ts");

        Assert.Contains("record.submitted_by ??= context.user.personId ?? null;", auto, StringComparison.Ordinal);

        // And wired, on create only: re-filling "who created this" on every edit would rewrite the
        // record's own history to whoever touched it last.
        var app = Build.Content(result, "api/src/app.ts");
        Assert.Contains("beforeCreate: [fillExpenseClaim]", app, StringComparison.Ordinal);
        Assert.DoesNotContain("beforeUpdate: [fillExpenseClaim]", app, StringComparison.Ordinal);
    }

    /// <summary>A computed field becomes a function, with the expression the definition wrote
    /// beside it and the dependency order made explicit.</summary>
    [Fact]
    public void A_computed_field_becomes_a_function()
    {
        var computed = Build.Corpus()
            .Select(key => Build.Generate(key).Files
                .FirstOrDefault(f => f.RelativePath.StartsWith("api/src/computed/", StringComparison.Ordinal)))
            .FirstOrDefault(f => f is not null);

        Assert.True(computed is not null, "No corpus application produced a computed field.");

        Assert.Contains("computedField<", computed!.Content, StringComparison.Ordinal);
        Assert.Contains("computeAll<", computed.Content, StringComparison.Ordinal);
        Assert.Contains("weekStartsMonday", computed.Content, StringComparison.Ordinal);
    }

    /// <summary>A rollup is REPORTED rather than emitted as a column that stays empty and looks
    /// like a data problem.</summary>
    [Fact]
    public void A_rollup_is_reported_rather_than_left_empty()
    {
        var reported = Build.Corpus()
            .SelectMany(key => Build.Generate(key).Warnings)
            .Where(w => w.Code == "CORD2305")
            .ToList();

        Assert.NotEmpty(reported);
        Assert.Contains(reported, w => w.Message.Contains("rollup", StringComparison.Ordinal));
        Assert.All(reported, w => Assert.Contains("stays empty", w.Message, StringComparison.Ordinal));
    }

    /// <summary>A workflow is reported too. An application whose approvals silently never fire is
    /// the failure this diagnostic exists to prevent.</summary>
    [Fact]
    public void A_workflow_is_reported_as_not_generated_yet()
    {
        var reported = Build.Corpus()
            .SelectMany(key => Build.Generate(key).Warnings)
            .Where(w => w.Code == "CORD2302")
            .ToList();

        Assert.NotEmpty(reported);
        Assert.All(reported, w => Assert.Contains("node-vue", w.Message, StringComparison.Ordinal));
    }

    /// <summary>
    /// A diagnostic names the target that raised it.
    ///
    /// <para>The web emitter is shared, and it said "the dotnet-vue generator" unconditionally —
    /// true while there was one target, and a lie the day there were two. A node-vue user was told
    /// about a gap in a generator they had not asked for and could not have chosen differently.
    /// </para>
    /// </summary>
    [Fact]
    public void No_diagnostic_names_the_other_target()
    {
        foreach (var key in Build.Corpus())
            foreach (var warning in Build.Generate(key).Warnings)
                Assert.DoesNotContain("dotnet-vue", warning.Message, StringComparison.Ordinal);
    }
}

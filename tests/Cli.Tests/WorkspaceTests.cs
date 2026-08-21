// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Cli.Commands;
using Cordango.Cli.Workspace;
using Cordango.Cord;
using Cordango.Definition;

namespace Cordango.Cli.Tests;

/// <summary>
/// CordyOSS phase 0 and phase 1 acceptance, as tests.
///
/// <para>Each one is named for the promise it keeps rather than the method it calls, because the
/// promises are what a reader of the plan is checking against.</para>
/// </summary>
public class WorkspaceTests
{
    // ---- phase 0: the command and source contract -------------------------------------------------

    [Fact]
    public void The_scaffold_a_new_workspace_ships_with_already_passes_check()
    {
        using var sandbox = new Sandbox();

        Assert.Equal(ExitCodes.Ok, sandbox.Run("new", "expense-approval"));
        Assert.Equal(ExitCodes.Ok, sandbox.Run("check"));

        // Not merely coherent. `cordango new` handing somebody an app that is already incomplete would
        // teach them that "incomplete" is the normal resting state, and the distinction would stop
        // carrying information on the day it matters.
        var (_, payload) = sandbox.RunJson("check");
        var app = (JsonObject)payload["apps"]!.AsArray()[0]!;
        Assert.True((bool)app["coherent"]!);
        Assert.True((bool)app["valid"]!, sandbox.Out);
    }

    [Fact]
    public void A_new_workspace_needs_no_database_and_no_model()
    {
        // Nothing to assert directly — the claim is enforced by AppBuilder.Oss.slnx's project set and
        // by this test project referencing neither Platform nor Generator. What IS checked is that the
        // whole phase-0 journey runs to completion in a bare temp directory with no services up.
        using var sandbox = new Sandbox();

        Assert.Equal(ExitCodes.Ok, sandbox.Run("new", "claims"));
        Assert.Equal(ExitCodes.Ok, sandbox.Run("check"));
        Assert.Equal(ExitCodes.Ok, sandbox.Run("build"));
        Assert.Equal(ExitCodes.Ok, sandbox.Run("inspect"));
        Assert.Equal(ExitCodes.Ok, sandbox.Run("doctor"));
    }

    [Fact]
    public void Equal_source_produces_an_equal_definition_hash()
    {
        using var sandbox = new Sandbox();
        sandbox.Run("new", "claims");

        Assert.Equal(ExitCodes.Ok, sandbox.Run("build"));
        var first = File.ReadAllText(sandbox.Path_(".cordango", "build", "claims", "app.definition.json"));
        var firstManifest = File.ReadAllText(sandbox.Path_(".cordango", "build", "claims", "manifest.json"));

        Assert.Equal(ExitCodes.Ok, sandbox.Run("build"));
        var second = File.ReadAllText(sandbox.Path_(".cordango", "build", "claims", "app.definition.json"));
        var secondManifest = File.ReadAllText(sandbox.Path_(".cordango", "build", "claims", "manifest.json"));

        Assert.Equal(first, second);

        // The manifest too, which is the one that would silently drift: a wall-clock build time is the
        // obvious thing to stamp into it and would break reproducibility on the second run.
        Assert.Equal(firstManifest, secondManifest);
    }

    [Fact]
    public void Two_apps_live_in_one_workspace_and_are_checked_together()
    {
        using var sandbox = new Sandbox();
        sandbox.Run("new", "expense-approval");

        Assert.Equal(ExitCodes.Ok, sandbox.Run("add", "app", "crm"));

        var (_, payload) = sandbox.RunJson("check");
        Assert.Equal(["expense_approval", "crm"],
            payload["apps"]!.AsArray().Select(a => (string?)a!["app"]));
    }

    [Fact]
    public void An_app_directory_nobody_registered_is_reported_and_not_installed()
    {
        using var sandbox = new Sandbox();
        sandbox.Run("new", "claims");

        Directory.CreateDirectory(sandbox.Path_("apps", "stowaway"));

        // Not built, not checked...
        var (_, checkPayload) = sandbox.RunJson("check");
        Assert.Single(checkPayload["apps"]!.AsArray());

        // ...but not silent either.
        var (exit, doctorPayload) = sandbox.RunJson("doctor");
        Assert.Equal(ExitCodes.Failed, exit);
        Assert.Contains(doctorPayload["findings"]!.AsArray().Select(f => (string?)f),
            f => f!.Contains("stowaway", StringComparison.Ordinal));
    }

    [Fact]
    public void New_refuses_a_directory_that_already_holds_something()
    {
        using var sandbox = new Sandbox();
        sandbox.Write("notes.txt", "mine");

        Assert.Equal(ExitCodes.Failed, sandbox.Run("new", "claims"));
        Assert.False(File.Exists(sandbox.Path_(WorkspaceFile.FileName)));

        // ...and says how to get a second app, because that is the question somebody actually has
        // at this moment. "Needs an empty directory" on its own reads as one-app-per-workspace,
        // which is the opposite of the design.
        Assert.Contains("cordango add app", sandbox.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void One_workspace_holds_many_apps_and_they_are_all_checked()
    {
        using var sandbox = new Sandbox();
        sandbox.Run("new", "expense-approval");

        Assert.Equal(ExitCodes.Ok, sandbox.Run("add", "app", "crm"));
        Assert.Equal(ExitCodes.Ok, sandbox.Run("add", "app", "helpdesk"));

        var (exit, payload) = sandbox.RunJson("check");

        Assert.Equal(ExitCodes.Ok, exit);
        Assert.Equal(["expense_approval", "crm", "helpdesk"],
            payload["apps"]!.AsArray().Select(a => (string?)a!["app"]));

        // Registration is what installs an app, not the directory existing — so cord.yaml lists all
        // three and the order it lists them in is the order everything reports them.
        var workspace = WorkspaceFile.Find(sandbox.Root, out _)!;
        Assert.Equal(["apps/expense-approval", "apps/crm", "apps/helpdesk"], workspace.Apps);
    }

    [Fact]
    public void New_tolerates_a_git_directory_because_git_init_first_is_a_reasonable_order()
    {
        using var sandbox = new Sandbox();
        Directory.CreateDirectory(sandbox.Path_(".git"));

        Assert.Equal(ExitCodes.Ok, sandbox.Run("new", "claims"));
    }

    // ---- phase 1: source, operations, and the equivalence between them -----------------------------

    [Fact]
    public void Parse_write_parse_is_semantically_identical()
    {
        using var sandbox = new Sandbox();
        sandbox.Run("new", "claims");

        var workspace = WorkspaceFile.Find(sandbox.Root, out _)!;
        var loaded = AppFolder.Load(workspace.Root, workspace.Apps[0]);
        Assert.True(loaded.Ok, string.Join("\n", loaded.Problems));

        var (reread, problems) = CordSource.Read(CordSource.Write(loaded.App!));

        Assert.Empty(problems);
        Assert.Equal(
            DefinitionHash.Of(CordLower.Lower(loaded.App!)),
            DefinitionHash.Of(CordLower.Lower(reread!)));
    }

    [Fact]
    public void A_direct_edit_and_an_operation_produce_the_same_app()
    {
        // The claim that makes a file-based workspace safe: an agent may hand-edit source OR call an
        // operation, and the two converge. If they did not, review of a diff would tell you nothing
        // about what the operation path would have built.
        using var sandbox = new Sandbox();
        sandbox.Run("new", "claims");

        var viaOperation = ApplyOps(sandbox, "domain", """
            [{"op":"upsert_field","entity":"task","field":
              {"key":"owner","label":"Owner","type":"text"}}]
            """);
        Assert.Equal(ExitCodes.Ok, viaOperation);

        var operationFiles = sandbox.Snapshot();
        var operationHash = DefinitionHash.Of(CordLower.Lower(LoadOnly(sandbox).App!));

        // Now the same change by hand, from the pre-change state.
        using var byHand = new Sandbox();
        byHand.Run("new", "claims");

        // A hand edit is what somebody would actually type: a few lines of YAML appended under
        // `fields:`, deliberately with different indentation and quoting from the canonical form.
        var entityFile = byHand.Path_("apps", "claims", "entities", "task.cordango.yaml");
        File.WriteAllText(entityFile, File.ReadAllText(entityFile).TrimEnd('\n')
            + "\n  owner:\n    label: \"Owner\"\n    type: text\n");

        // Formatting differs — the hand edit is not canonical — so the comparison is the MODEL, which
        // is the thing that has to agree. `cordango fmt` then makes the bytes agree too.
        Assert.Equal(operationHash, DefinitionHash.Of(CordLower.Lower(LoadOnly(byHand).App!)));

        Assert.Equal(ExitCodes.Ok, byHand.Run("fmt"));
        Assert.Equal(operationFiles, byHand.Snapshot());
    }

    [Fact]
    public void An_operation_naming_another_aggregate_of_the_same_kind_is_refused_and_changes_nothing()
    {
        using var sandbox = new Sandbox();
        sandbox.Run("new", "claims");
        var before = sandbox.Snapshot();

        // A perfectly legal screen operation, under a scope opened for a DIFFERENT screen. This is
        // the refusal that needs a scope to catch it: the vocabulary check below cannot, because
        // upsert_screen is exactly what a screen scope is for.
        var exit = ApplyOps(sandbox, "screen:tasks", """
            [{"op":"upsert_screen","screen":{"key":"elsewhere","label":"Elsewhere",
              "sections":[{"key":"all","kind":"list","of":"task","label":"All"}]}}]
            """);

        Assert.Equal(ExitCodes.Failed, exit);
        Assert.Equal(before, sandbox.Snapshot());
        Assert.Contains("scoped to", sandbox.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_operation_from_another_concerns_vocabulary_is_refused_and_changes_nothing()
    {
        using var sandbox = new Sandbox();
        sandbox.Run("new", "claims");
        var before = sandbox.Snapshot();

        // Refused one step earlier than the test above — the domain scope does not offer
        // `upsert_screen` at all, so it never reaches the aggregate check. Both refusals matter:
        // this one is what stops a scope being used to reach another concern's vocabulary.
        var exit = ApplyOps(sandbox, "domain", """
            [{"op":"upsert_screen","screen":{"key":"smuggled","label":"Smuggled",
              "sections":[{"key":"all","kind":"list","of":"task","label":"All"}]}}]
            """);

        Assert.Equal(ExitCodes.Failed, exit);
        Assert.Equal(before, sandbox.Snapshot());
        Assert.Contains("is not an operation this tool accepts", sandbox.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_operation_whose_result_would_not_hold_together_is_refused_and_changes_nothing()
    {
        using var sandbox = new Sandbox();
        sandbox.Run("new", "claims");
        var before = sandbox.Snapshot();

        // A reference to an entity that does not exist. Cord itself catches this one, which is the
        // point: the refusal happens before anything reaches the filesystem.
        var exit = ApplyOps(sandbox, "domain", """
            [{"op":"upsert_field","entity":"task","field":
              {"key":"client","label":"Client","type":"reference","target":"nonexistent"}}]
            """);

        Assert.Equal(ExitCodes.Failed, exit);
        Assert.Equal(before, sandbox.Snapshot());
    }

    [Fact]
    public void A_malformed_operation_is_refused_and_changes_nothing()
    {
        using var sandbox = new Sandbox();
        sandbox.Run("new", "claims");
        var before = sandbox.Snapshot();

        Assert.Equal(ExitCodes.Failed, ApplyOps(sandbox, "domain",
            """[{"op":"upsert_entity","entity":{"label":"No key"}}]"""));
        Assert.Equal(before, sandbox.Snapshot());
    }

    [Fact]
    public void A_change_to_one_aggregate_rewrites_one_file()
    {
        using var sandbox = new Sandbox();
        sandbox.Run("new", "claims");

        File.WriteAllText(sandbox.Path_("ops.json"), """
            [{"op":"upsert_field","entity":"task","field":
              {"key":"owner","label":"Owner","type":"text"}}]
            """);

        var (exit, payload) = sandbox.RunJson("apply", "ops.json", "--app", "claims", "--scope", "domain");

        Assert.Equal(ExitCodes.Ok, exit);
        Assert.Equal(["apps/claims/entities/task.cordango.yaml"],
            payload["written"]!.AsArray().Select(p => (string?)p));
    }

    [Fact]
    public void Removing_an_entity_removes_its_file()
    {
        using var sandbox = new Sandbox();
        sandbox.Run("new", "claims");

        Assert.Equal(ExitCodes.Ok, ApplyOps(sandbox, "domain", """
            [{"op":"upsert_entity","entity":{"key":"tag","label":"Tag","displayField":"name",
              "fields":[{"key":"name","label":"Name","type":"text","required":true}]}}]
            """));
        Assert.True(File.Exists(sandbox.Path_("apps", "claims", "entities", "tag.cordango.yaml")));

        Assert.Equal(ExitCodes.Ok, ApplyOps(sandbox, "domain", """[{"op":"remove","entity":"tag"}]"""));

        Assert.False(File.Exists(sandbox.Path_("apps", "claims", "entities", "tag.cordango.yaml")));

        // The one that survived is still there — a removal must not take the directory with it.
        Assert.True(File.Exists(sandbox.Path_("apps", "claims", "entities", "task.cordango.yaml")));
        Assert.Equal(ExitCodes.Ok, sandbox.Run("check"));
    }

    [Fact]
    public void An_app_cannot_be_emptied_out_from_under_itself()
    {
        // The starter screen points at `task`, so removing the entity leaves a screen aimed at
        // nothing; and the gate requires at least one entity regardless. Refused on COHERENCE rather
        // than scope — the case where "the operation was legal" and "the result is an application"
        // come apart, and the reason apply validates rather than trusting Cord's own checks.
        using var sandbox = new Sandbox();
        sandbox.Run("new", "claims");
        var before = sandbox.Snapshot();

        Assert.Equal(ExitCodes.Failed, ApplyOps(sandbox, "domain", """[{"op":"remove","entity":"task"}]"""));
        Assert.Equal(before, sandbox.Snapshot());
    }

    [Fact]
    public void Removing_the_last_screen_leaves_an_app_that_is_coherent_but_unfinished()
    {
        using var sandbox = new Sandbox();
        sandbox.Run("new", "claims");

        Assert.Equal(ExitCodes.Ok, ApplyOps(sandbox, "screen:tasks",
            """[{"op":"remove_screen","key":"tasks"}]"""));

        Assert.False(File.Exists(sandbox.Path_("apps", "claims", "views", "screens", "tasks.cordango.yaml")));

        // `check` still passes: nothing to land on is unfinished, not broken. If this ever starts
        // failing, incremental authoring has become impossible and the two thresholds have collapsed
        // back into one.
        var (exit, payload) = sandbox.RunJson("check");
        Assert.Equal(ExitCodes.Ok, exit);

        var app = (JsonObject)payload["apps"]!.AsArray()[0]!;
        Assert.True((bool)app["coherent"]!);
        Assert.False((bool)app["valid"]!);
    }

    [Fact]
    public void Dry_run_reports_the_same_files_the_real_thing_would_write_and_writes_none()
    {
        using var sandbox = new Sandbox();
        sandbox.Run("new", "claims");
        var before = sandbox.Snapshot();

        File.WriteAllText(sandbox.Path_("ops.json"), """
            [{"op":"upsert_field","entity":"task","field":
              {"key":"owner","label":"Owner","type":"text"}}]
            """);

        var (dryExit, dry) = sandbox.RunJson(
            "apply", "ops.json", "--app", "claims", "--scope", "domain", "--dry-run");
        Assert.Equal(ExitCodes.Ok, dryExit);
        Assert.Equal(before, sandbox.Snapshot());

        var (_, real) = sandbox.RunJson("apply", "ops.json", "--app", "claims", "--scope", "domain");
        Assert.Equal(
            dry["written"]!.AsArray().Select(p => (string?)p),
            real["written"]!.AsArray().Select(p => (string?)p));
    }

    [Fact]
    public void Fmt_check_reports_drift_without_fixing_it()
    {
        using var sandbox = new Sandbox();
        sandbox.Run("new", "claims");

        var path = sandbox.Path_("apps", "claims", "entities", "task.cordango.yaml");
        var canonical = File.ReadAllText(path);
        // Legal YAML, not canonical YAML: four-space indent and quoted scalars that need no quotes.
        File.WriteAllText(path, canonical.Replace("  ", "    ").Replace("label: Task", "label: 'Task'"));

        Assert.Equal(ExitCodes.Failed, sandbox.Run("fmt", "--check"));
        Assert.NotEqual(canonical, File.ReadAllText(path));

        Assert.Equal(ExitCodes.Ok, sandbox.Run("fmt"));
        Assert.Equal(canonical, File.ReadAllText(path));
    }

    [Fact]
    public void A_file_the_app_does_not_list_is_reported_rather_than_ignored()
    {
        // Declarative source cannot smuggle a domain change into a screen's file the way an operation
        // batch could — a file says what ONE aggregate is, and nothing else. The equivalent hazard is
        // the opposite one: an aggregate on disk that the app's `order` never names, which a silent
        // reader would simply not load. A pulled commit adding an entity is exactly that case.
        using var sandbox = new Sandbox();
        sandbox.Run("new", "claims");

        sandbox.Write("apps/claims/entities/stowaway.cordango.yaml", """
            entity: stowaway
            label: Stowaway
            fields:
              name:
                label: Name
                type: text
            """);

        Assert.Equal(ExitCodes.Failed, sandbox.Run("check"));
        Assert.Contains("stowaway", sandbox.Error, StringComparison.Ordinal);
        Assert.Contains("order", sandbox.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Inspect_answers_about_one_aggregate_rather_than_the_whole_app()
    {
        using var sandbox = new Sandbox();
        sandbox.Run("new", "claims");

        var (exit, payload) = sandbox.RunJson("inspect", "entities/task", "--app", "claims");

        Assert.Equal(ExitCodes.Ok, exit);
        var description = (string)payload["description"]!;
        Assert.Contains("task", description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("$defs", description, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("domain", "domain", null)]
    [InlineData("screen:claims", "screen", "claims")]
    [InlineData("tab:claims/inbox", "tab", "claims/inbox")]
    public void Scopes_parse(string text, string kind, string? key)
    {
        var scope = ApplyCommand.ParseScope(text);
        Assert.Equal(new CordAggregateRef(kind, key), scope);
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("screen:")]
    public void Nonsense_scopes_do_not(string text) => Assert.Null(ApplyCommand.ParseScope(text));

    [Theory]
    [InlineData("expense-approval", "expense_approval")]
    [InlineData("CRM", "crm")]
    [InlineData("1099-filing", "app_1099_filing")]
    public void A_directory_name_becomes_a_stable_key(string directory, string key) =>
        Assert.Equal(key, Templates.Scaffold.KeyFor(directory));

    // ---- helpers -----------------------------------------------------------------------------------

    private static int ApplyOps(Sandbox sandbox, string scope, string ops)
    {
        var path = Path.Combine(sandbox.Root, "ops.json");
        File.WriteAllText(path, ops);
        return sandbox.Run("apply", path, "--app", "claims", "--scope", scope);
    }

    private static LoadedApp LoadOnly(Sandbox sandbox)
    {
        var workspace = WorkspaceFile.Find(sandbox.Root, out _)!;
        return AppFolder.Load(workspace.Root, workspace.Apps[0]);
    }
}

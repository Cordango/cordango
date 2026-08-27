// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Cli.Remote;
using Cordango.Cli.Workspace;

namespace Cordango.Cli.Tests;

public sealed class ImportTests
{
    [Fact]
    public void Importing_a_file_needs_no_connection_and_makes_no_call()
    {
        using var cord = new Sandbox();
        using var instance = new FakeInstance().With("a1", "task-manager", "Task Manager", Corpus("task-manager"));
        cord.Run("new", "placeholder");
        Connect(cord, instance);

        File.WriteAllText(cord.Path_("hand.json"), Corpus("task-manager").ToJsonString());

        Assert.Equal(ExitCodes.Ok, cord.Run("import", "hand.json"));
        Assert.Equal(0, instance.ListCalls);
    }

    [Fact]
    public void Asking_for_the_apps_with_nothing_to_ask_says_how_to_connect()
    {
        using var cord = new Sandbox();
        cord.Run("new", "placeholder");

        var exit = cord.Run("import");

        Assert.Equal(ExitCodes.NoInstance, exit);
        Assert.Contains("not connected to an instance", cord.Error, StringComparison.Ordinal);
        Assert.Contains("app.definition.json", cord.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_that_is_not_a_file_is_still_a_missing_file_when_nothing_is_connected()
    {
        using var cord = new Sandbox();
        cord.Run("new", "placeholder");

        var exit = cord.Run("import", "support");

        Assert.Equal(ExitCodes.Failed, exit);
        Assert.Contains("no such file: support", cord.Error, StringComparison.Ordinal);
        Assert.Contains("cordango login", cord.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_list_is_what_the_instance_says_it_is()
    {
        using var cord = new Sandbox();
        using var instance = new FakeInstance()
            .With("a1", "task-manager", "Task Manager", null, entities: 4)
            .With("a2", "helpdesk", "Helpdesk", null, status: "generated", version: "0.2.0");

        cord.Run("new", "placeholder");
        Connect(cord, instance);

        var (exit, payload) = cord.RunJson("import", "--list");

        Assert.Equal(ExitCodes.Ok, exit);
        Assert.Equal(instance.Origin, (string?)payload["instance"]);
        Assert.Equal(["task-manager", "helpdesk"],
            payload["apps"]!.AsArray().Select(a => (string?)a!["handle"]));
        Assert.False(Directory.Exists(cord.Path_("apps", "task-manager")));
    }

    [Fact]
    public void Naming_one_imports_it_as_source()
    {
        using var cord = new Sandbox();
        using var instance = new FakeInstance()
            .With("a1", "task-manager", "Task Manager", Corpus("task-manager"))
            .With("a2", "helpdesk", "Helpdesk", Corpus("helpdesk"));

        cord.Run("new", "placeholder");
        Connect(cord, instance);

        var (exit, payload) = cord.RunJson("import", "helpdesk");

        Assert.Equal(ExitCodes.Ok, exit);
        Assert.Equal("apps/helpdesk", (string?)payload["path"]);
        Assert.Equal(instance.Origin, (string?)payload["from"]!["instance"]);
        Assert.True((bool)payload["check"]!["coherent"]!);
        Assert.True(Directory.EnumerateFiles(cord.Path_("apps", "helpdesk"), "*.cordango.yaml",
            SearchOption.AllDirectories).Any());

        Assert.Contains("apps/helpdesk", WorkspaceFile.Find(cord.Root, out _)!.Apps);
    }

    [Fact]
    public void Picking_from_the_list_imports_the_one_that_was_picked()
    {
        using var cord = new Sandbox();
        using var instance = new FakeInstance()
            .With("a1", "task-manager", "Task Manager", Corpus("task-manager"))
            .With("a2", "helpdesk", "Helpdesk", Corpus("helpdesk"));

        cord.Run("new", "placeholder");
        Connect(cord, instance);

        Assert.Equal(ExitCodes.Ok, cord.RunAnswering("2\n", "import"));

        Assert.True(Directory.Exists(cord.Path_("apps", "helpdesk")));
        Assert.False(Directory.Exists(cord.Path_("apps", "task-manager")));
    }

    [Fact]
    public void With_nobody_to_ask_it_names_the_apps_it_would_have_offered()
    {
        using var cord = new Sandbox();
        using var instance = new FakeInstance()
            .With("a1", "task-manager", "Task Manager", null)
            .With("a2", "helpdesk", "Helpdesk", null);

        cord.Run("new", "placeholder");
        Connect(cord, instance);

        var (exit, payload) = cord.RunJson("import");

        Assert.Equal(ExitCodes.Usage, exit);
        Assert.Equal(2, payload["apps"]!.AsArray().Count);
    }

    [Fact]
    public void A_name_the_instance_does_not_have_lists_the_ones_it_does()
    {
        using var cord = new Sandbox();
        using var instance = new FakeInstance().With("a1", "task-manager", "Task Manager", null);

        cord.Run("new", "placeholder");
        Connect(cord, instance);

        var exit = cord.Run("import", "invoices");

        Assert.Equal(ExitCodes.Failed, exit);
        Assert.Contains("no app called 'invoices'", cord.Error, StringComparison.Ordinal);
        Assert.Contains("task-manager", cord.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_app_the_instance_never_gave_a_definition_is_named_rather_than_half_imported()
    {
        using var cord = new Sandbox();
        using var instance = new FakeInstance().With("a1", "task-manager", "Task Manager", definition: null);

        cord.Run("new", "placeholder");
        Connect(cord, instance);

        var exit = cord.Run("import", "task-manager");

        Assert.Equal(ExitCodes.Failed, exit);
        Assert.Contains("has no definition", cord.Error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(cord.Path_("apps", "task-manager")));
    }

    [Fact]
    public void Importing_the_same_app_twice_refuses_rather_than_merging()
    {
        using var cord = new Sandbox();
        using var instance = new FakeInstance().With("a1", "task-manager", "Task Manager", Corpus("task-manager"));

        cord.Run("new", "placeholder");
        Connect(cord, instance);

        Assert.Equal(ExitCodes.Ok, cord.Run("import", "task-manager"));

        var exit = cord.Run("import", "task-manager");

        Assert.Equal(ExitCodes.Failed, exit);
        Assert.Contains("already exists", cord.Error, StringComparison.Ordinal);
        Assert.Contains("--app", cord.Error, StringComparison.Ordinal);
    }

    private static void Connect(Sandbox cord, FakeInstance instance)
    {
        var workspace = WorkspaceFile.Find(cord.Root, out _)!;
        var credentials = Credentials.Load();
        credentials.Save(new InstanceLogin(instance.Origin, "cord_pat.a.b.c", "default", "t@example.com",
            DateTimeOffset.UtcNow));
        credentials.Bind(workspace.WorkspaceId, instance.Origin);
        credentials.Flush();
    }

    private static JsonObject Corpus(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "corpus", "reference",
                name + ".appdef.json");
            if (File.Exists(candidate))
                return (JsonObject)JsonNode.Parse(File.ReadAllText(candidate))!;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find the corpus above " + AppContext.BaseDirectory);
    }
}

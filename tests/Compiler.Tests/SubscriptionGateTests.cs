// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Compile;
using Cordango.Definition;

namespace Cordango.Compiler.Tests;

public class SubscriptionGateTests
{
    private static JsonObject Def(string workflow)
    {
        return (JsonObject)JsonNode.Parse($$"""
        {
          "schemaVersion":"2.0","key":"budget_tracker","name":"Budget Tracker","version":"1.0.0",
          "entities":[
            {"key":"spend_entry","label":"Spend","labelPlural":"Spend","displayField":"description","fields":[
              {"key":"description","label":"Description","type":"text"},
              {"key":"amount","label":"Amount","type":"money","currency":"EUR"}
            ]}
          ],
          "workflows":[{{workflow}}]
        }
        """)!;
    }

    private static KnownApps Workspace => KnownApps.Of([
        new KnownApp("purchase_requests", "Purchase Requests", ["purchase_request", "request_item"]),
    ]);

    private const string Subscribe = """
        {"key":"commit","name":"Commit on approval",
         "trigger":{"event":"command.emitted","app":"purchase_requests","name":"purchase.approved"},
         "effects":[{"type":"createRecord","entity":"spend_entry",
                     "set":{"description":"{{record.title}}","amount":"{{record.amount}}"}}]}
    """;

    [Fact]
    public void A_subscription_to_a_sibling_app_holds()
    {
        Assert.Empty(Gate.SemanticErrors(Def(Subscribe), Workspace));
    }

    [Fact]
    public void A_subscription_stands_when_the_source_app_is_not_here_yet()
    {
        Assert.Empty(Gate.SemanticErrors(Def(Subscribe), KnownApps.Of([])));
    }

    [Fact]
    public void A_subscription_to_this_app_itself_is_refused()
    {
        var errors = Gate.SemanticErrors(Def("""
            {"key":"commit","name":"Commit",
             "trigger":{"event":"command.emitted","app":"budget_tracker","name":"spend.paid"},
             "effects":[{"type":"createRecord","entity":"spend_entry","set":{"description":"x"}}]}
        """), Workspace);

        Assert.Contains(errors, e => e.Contains("which is this app"));
    }

    [Fact]
    public void A_subscription_naming_an_entity_the_source_does_not_have_is_refused()
    {
        var errors = Gate.SemanticErrors(Def("""
            {"key":"commit","name":"Commit",
             "trigger":{"event":"record.created","app":"purchase_requests","entity":"invoice"},
             "effects":[{"type":"createRecord","entity":"spend_entry","set":{"description":"x"}}]}
        """), Workspace);

        Assert.Contains(errors, e => e.Contains("entity 'invoice' in app 'purchase_requests'"));
    }

    [Fact]
    public void A_subscription_may_create_a_local_record()
    {
        Assert.Empty(Gate.SemanticErrors(Def(Subscribe), Workspace));
    }

    [Fact]
    public void A_subscription_creating_an_unknown_local_entity_is_refused()
    {
        var errors = Gate.SemanticErrors(Def("""
            {"key":"commit","name":"Commit",
             "trigger":{"event":"command.emitted","app":"purchase_requests","name":"purchase.approved"},
             "effects":[{"type":"createRecord","entity":"ledger_line","set":{"description":"x"}}]}
        """), Workspace);

        Assert.Contains(errors, e => e.Contains("creates unknown entity 'ledger_line'"));
    }

    [Fact]
    public void A_cross_app_write_without_a_subscription_is_refused()
    {
        var errors = Gate.SemanticErrors(Def("""
            {"key":"push","name":"Push",
             "trigger":{"event":"record.created","entity":"spend_entry"},
             "effects":[{"type":"createRecord","app":"purchase_requests","entity":"purchase_request",
                         "set":{"title":"x"}}]}
        """), Workspace);

        Assert.Contains(errors, e => e.Contains("is not subscribed to anything"));
    }

    [Fact]
    public void A_cross_app_write_from_a_subscription_holds()
    {
        Assert.Empty(Gate.SemanticErrors(Def("""
            {"key":"echo","name":"Echo",
             "trigger":{"event":"command.emitted","app":"purchase_requests","name":"purchase.approved"},
             "effects":[{"type":"createRecord","app":"purchase_requests","entity":"request_item",
                         "set":{"description":"x"}}]}
        """), Workspace));
    }

    [Fact]
    public void A_cross_app_write_into_an_entity_the_target_does_not_have_is_refused()
    {
        var errors = Gate.SemanticErrors(Def("""
            {"key":"echo","name":"Echo",
             "trigger":{"event":"command.emitted","app":"purchase_requests","name":"purchase.approved"},
             "effects":[{"type":"createRecord","app":"purchase_requests","entity":"invoice",
                         "set":{"description":"x"}}]}
        """), Workspace);

        Assert.Contains(errors, e => e.Contains("writes unknown entity 'invoice' in app 'purchase_requests'"));
    }

    [Fact]
    public void A_cross_app_write_naming_this_app_is_refused()
    {
        var errors = Gate.SemanticErrors(Def("""
            {"key":"echo","name":"Echo",
             "trigger":{"event":"command.emitted","app":"purchase_requests","name":"purchase.approved"},
             "effects":[{"type":"createRecord","app":"budget_tracker","entity":"spend_entry",
                         "set":{"description":"x"}}]}
        """), Workspace);

        Assert.Contains(errors, e => e.Contains("which is this app"));
    }

    [Fact]
    public void A_subscription_appears_as_a_dependency_the_author_never_declared()
    {
        var notes = AppDependencies.Diagnose(Def(Subscribe));

        var note = Assert.Single(notes, n => n.Code == "dependency.implicit");
        Assert.Contains("purchase_requests", note.Message);
    }

    [Fact]
    public void A_declared_subscription_raises_no_note()
    {
        var def = Def(Subscribe);
        def["uses"] = JsonNode.Parse("""[{"app":"purchase_requests","why":"we commit its approvals"}]""");

        Assert.Empty(AppDependencies.Diagnose(def));
    }

    [Fact]
    public void A_local_workflow_is_unaffected()
    {
        Assert.Empty(Gate.SemanticErrors(Def("""
            {"key":"stamp","name":"Stamp",
             "trigger":{"event":"record.created","entity":"spend_entry"},
             "effects":[{"type":"updateRecord","set":{"description":"stamped"}}]}
        """), Workspace));
    }
}

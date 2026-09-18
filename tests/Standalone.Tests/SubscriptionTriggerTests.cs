// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Compile;
using Cordango.Definition;
using Cordango.SourceGen;
using Cordango.SourceGen.Common;
using Cordango.SourceGen.DotNet;
using Cordango.SourceGen.DotNet.Emit;

namespace Cordango.Standalone.Tests;

/// <summary>
/// The two triggers that let one part of an application react to another without knowing about it:
/// <c>command.emitted</c> and <c>process.state_entered</c>.
///
/// <para><b>Both name an ANNOUNCEMENT rather than an entity</b>, and that asymmetry is the design. A
/// command declares what it announces and stops; a workflow names the announcement back. Neither
/// side imports the other, so two apps of a workspace can be composed without becoming dependencies
/// — and once linked they are in one process, so the announcement is a method call.</para>
/// </summary>
public class SubscriptionTriggerTests
{
    [Fact]
    public void A_subscription_resolves_to_the_entity_whose_command_announces()
    {
        var emitted = Workflows(App());

        Assert.Contains("\"on_planned\", \"On planned\", \"project\", WorkflowEvent.CommandEmitted",
            emitted, StringComparison.Ordinal);
        Assert.Contains("Announcement = \"project.planned\"", emitted, StringComparison.Ordinal);
    }

    /// <summary>The announcing side. A name and nothing else — the command has no idea who is
    /// listening.</summary>
    [Fact]
    public void The_announcing_command_carries_the_name_it_announces()
    {
        Assert.Contains("Emits = [\"project.planned\"]", Commands(App()), StringComparison.Ordinal);
    }

    /// <summary>A state subscription resolves to the lifecycle's own status field, so that "entered a
    /// state" is the field becoming that value — the same machinery `field.changed` uses, rather than
    /// a second answer to "did it really change".</summary>
    [Fact]
    public void A_state_subscription_resolves_to_the_lifecycle_field_and_the_state()
    {
        var emitted = Workflows(App(subscribe: """
        {"event":"process.state_entered","name":"project.active"}
        """));

        Assert.Contains("\"project\", WorkflowEvent.StateEntered", emitted, StringComparison.Ordinal);
        Assert.Contains("Field: \"status\"", emitted, StringComparison.Ordinal);
        Assert.Contains("State = \"active\"", emitted, StringComparison.Ordinal);
    }

    /// <summary>A subscription to a name nothing announces never runs, and looks entirely finished
    /// from the outside. Reported rather than emitted.</summary>
    [Fact]
    public void A_subscription_to_an_announcement_nobody_makes_is_reported()
    {
        var result = Build(App(subscribe: """
        {"event":"command.emitted","name":"project.abandoned"}
        """));

        Assert.Contains(result.Warnings,
            d => d.Code == NotYetCodes.Trigger
                && d.Message.Contains("project.abandoned", StringComparison.Ordinal)
                && d.Message.Contains("nothing in this build announces it", StringComparison.Ordinal));
    }

    [Fact]
    public void A_state_subscription_to_an_entity_with_no_lifecycle_is_reported()
    {
        var result = Build(App(subscribe: """
        {"event":"process.state_entered","name":"note.filed"}
        """));

        Assert.Contains(result.Warnings,
            d => d.Code == NotYetCodes.Trigger && d.Message.Contains("note.filed", StringComparison.Ordinal));
    }

    /// <summary>
    /// The bug this test exists for: <c>EventName</c> had a <c>_ =&gt; "Schedule"</c> default, so the
    /// day the two new triggers joined the supported set they were both emitted as SCHEDULED
    /// workflows with no cron — which never run, generated in silence.
    /// </summary>
    [Fact]
    public void Every_supported_trigger_has_a_name_of_its_own()
    {
        var named = typeof(WorkflowEmitter)
            .GetMethod("EventName", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var trigger in WorkflowEmitter.Triggers)
        {
            var name = (string)named.Invoke(null, [trigger])!;
            Assert.True(seen.Add(name),
                $"'{trigger}' is emitted as WorkflowEvent.{name}, which another trigger already uses. "
                + "One of them fires as the other, and nothing reports it.");
        }
    }

    private static string Workflows(JsonObject definition) =>
        WorkflowEmitter.Workflows(Model(definition)).Content;

    private static string Commands(JsonObject definition) =>
        BackendEmitter.Commands(Model(definition)).Content;

    private static AppModel Model(JsonObject definition)
    {
        var manifest = AppCompiler.Compile(definition, "app1", At);
        return AppModel.From(
            new CompiledAppArtifact(manifest, manifest, "unhashed", new CompilerInfo("test", "1")));
    }

    private static GenerateResult Build(JsonObject definition)
    {
        var manifest = AppCompiler.Compile(definition, "app1", At);
        return new DotNetVueGenerator().Generate(new GenerateRequest(
            new CompiledAppArtifact(manifest, manifest, "unhashed", new CompilerInfo("test", "1")),
            new JsonObject { ["allowIncomplete"] = true, ["seed"] = 42 }));
    }

    private static readonly DateTimeOffset At = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    private const string Default = """{"event":"command.emitted","name":"project.planned"}""";

    private static JsonObject App(string? subscribe = null) => (JsonObject)JsonNode.Parse($$$$"""
    {
      "schemaVersion":"2.0","key":"app","name":"App","version":"1.0.0",
      "entities":[
        { "key":"project","label":"Project","labelPlural":"Projects","displayField":"title",
          "fields":[
            {"key":"title","label":"Title","type":"text"},
            {"key":"status","label":"Status","type":"select","role":"status",
             "options":[{"value":"draft","label":"Draft"},{"value":"active","label":"Active"}]}
          ] },
        { "key":"note","label":"Note","labelPlural":"Notes","displayField":"body",
          "fields":[{"key":"body","label":"Body","type":"text"}] }
      ],
      "processes":[
        { "key":"lifecycle","entity":"project","stateField":"status",
          "states":[{"key":"draft","label":"Draft"},{"key":"active","label":"Active"}],
          "transitions":[
            {"key":"plan","label":"Plan it","from":["draft"],"to":"active"}
          ] }
      ],
      "commands":[
        { "key":"plan_project","label":"Plan it","entity":"project",
          "transition":"plan","process":"lifecycle","emits":["project.planned"] }
      ],
      "workflows":[
        { "key":"on_planned","name":"On planned",
          "trigger":{{{{subscribe ?? Default}}}},
          "effects":[{"type":"createRecord","entity":"note","set":{"body":"{{record.title}}"}}] }
      ]
    }
    """)!;
}

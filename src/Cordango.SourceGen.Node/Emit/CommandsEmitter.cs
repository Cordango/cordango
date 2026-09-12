// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.SourceGen.Common;

namespace Cordango.SourceGen.Node.Emit;

/// <summary>
/// Everything a person can do to a record beyond editing its fields: approve it, close it, mark it
/// paid.
///
/// <para><b>The rules travel with the command.</b> Which states it may run from, what input it
/// needs, what it writes, and the guard that decides whether it applies at all — all of it is here,
/// as data, and the runtime enforces it from this table. The alternative is a route somebody wrote
/// a second copy of the rules into, which is the copy that gets out of date.</para>
///
/// <para><b>A guard is emitted as DATA, not as code, and that is a real difference from the .NET
/// target.</b> There, a condition has to become a C# expression, and a condition the emitter cannot
/// write has to be reported — because a command generated WITHOUT its guard would run in cases the
/// definition refuses, which is the one kind of generator bug that is invisible in the output. Here
/// the runtime carries the same evaluator the platform uses, so the condition travels as the object
/// the definition wrote and every guard the language can express is a guard this target enforces.
/// Nothing to report, and nothing to get subtly wrong in translation.</para>
/// </summary>
public static class CommandsEmitter
{
    public static GeneratedFile Emit(AppModel app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var source = new TsSource();

        source.Lines(Header.For(
            $"Everything a person can do to a {app.Name} record beyond editing its fields.",
            "Each command carries its own rules: which states it may run from, what input it needs, what\n"
            + "it writes and when it applies. The runtime checks them in that order — permission, then\n"
            + "legality, then guard, then input — so a command that will not run has touched nothing."));

        source.Line();
        source.Line("import { CommandCatalogue } from \"@cordango/standalone\";");
        source.Line();

        if (app.Commands.Count == 0)
        {
            source.Lines(TypeScript.Doc(
                "This definition declares no commands. Records are edited through the ordinary write "
                + "routes and there is nothing else to run.", 0));
            source.Line("export const appCommands = new CommandCatalogue([]);");
            return new GeneratedFile("api/src/commands.ts", source.ToString());
        }

        source.Line("export const appCommands = new CommandCatalogue([");
        source.Indent();

        foreach (var command in app.Commands) EmitCommand(source, app, command);

        source.Outdent();
        source.Line("]);");

        return new GeneratedFile("api/src/commands.ts", source.ToString());
    }

    private static void EmitCommand(TsSource source, AppModel app, CommandModel command)
    {
        var process = app.ProcessFor(command.Entity);
        var transition = process?.TransitionForCommand(command.Key);

        // `from` is an ARRAY. A transition that names one state still writes it as a list, and a
        // reader that treated a string as a state would match on its first character.
        var from = transition is null
            ? []
            : AppModel.Arr(transition["from"])
                .Select(f => AppModel.Str(f))
                .Where(f => f is not null)
                .Select(f => f!)
                .ToList();

        var to = transition is null ? null : AppModel.Str(transition["to"]);

        source.Line("{");
        source.Indent();

        source.Line($"key: {TypeScript.Literal(command.Key)},");
        source.Line($"label: {TypeScript.Literal(command.Label)},");
        source.Line($"entity: {TypeScript.Literal(command.Entity)},");

        if (transition is not null)
        {
            source.Line($"stateField: {TypeScript.Literal(process!.StateField)},");
            source.Line($"fromStates: [{string.Join(", ", from.Select(TypeScript.Literal))}],");
            if (to is not null) source.Line($"toState: {TypeScript.Literal(to)},");
        }

        if (command.InputFields.Count > 0)
            source.Line($"inputFields: [{string.Join(", ", command.InputFields.Select(TypeScript.Literal))}],");

        if (command.RequiredInputFields.Count > 0)
            source.Line(
                $"requiredInputFields: [{string.Join(", ", command.RequiredInputFields.Select(TypeScript.Literal))}],");

        var sets = command.Sets
            .Select(set => (Field: AppModel.Str(set["field"]), Value: AppModel.Str(set["value"]) ?? set["value"]?.ToString()))
            .Where(s => s.Field is not null)
            .Select(s => $"{{ field: {TypeScript.Literal(s.Field!)}, value: {TypeScript.Literal(s.Value)} }}")
            .ToList();

        if (sets.Count > 0) source.Line($"sets: [{string.Join(", ", sets)}],");

        if (command.SuccessMessage is { Length: > 0 })
            source.Line($"successMessage: {TypeScript.Literal(command.SuccessMessage)},");

        var notifications = command.Effects
            .Where(e => AppModel.Str(e["type"]) == "notify")
            .ToList();

        if (notifications.Count > 0)
        {
            source.Line("notifications: [");
            source.Indent();

            foreach (var notify in notifications)
            {
                var parts = new List<string>
                {
                    $"to: {TypeScript.Literal(AppModel.Str(notify["to"]) ?? "")}",
                    $"title: {TypeScript.Literal(AppModel.Str(notify["title"]) ?? command.Label)}",
                };

                if (AppModel.Str(notify["message"]) is { } message)
                    parts.Add($"message: {TypeScript.Literal(message)}");
                if (AppModel.Str(notify["link"]) is { } link)
                    parts.Add($"link: {TypeScript.Literal(link)}");

                source.Line($"{{ {string.Join(", ", parts)} }},");
            }

            source.Outdent();
            source.Line("],");
        }

        // The guard, last of the rules and the one a reader most often wants to find: everything
        // above it is what the command DOES, and this is when it may.
        if (command.Json["when"] is JsonObject guard && guard.Count > 0)
        {
            source.Line("// When this command applies. Evaluated by the runtime's own condition");
            source.Line("// evaluator — the same one the hosted platform uses, on the same fixtures.");
            source.Line($"when: {TypeScript.Value(guard, source.IndentWidth * 2)},");
        }

        source.Outdent();
        source.Line("},");
    }

    /// <summary>
    /// The effect types this target runs, beyond the notifications above.
    ///
    /// <para>Empty in this release, and reported rather than dropped: a command that silently did
    /// half of what the definition says would look like it worked. The generator raises CORD2303 for
    /// each one, and <c>--allow-incomplete</c> is how somebody says they know.</para>
    /// </summary>
    public static readonly IReadOnlySet<string> EmittedEffects =
        new HashSet<string>(StringComparer.Ordinal) { "notify" };
}

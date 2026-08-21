// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Cli.Commands;

public static class Help
{
    /// <summary>Commands this build actually has. `cordango help --json` is how an agent discovers the
    /// surface without a human pasting a README into its context.</summary>
    private static readonly (string Usage, string What)[] Commands =
    [
        ("new <first-app>", "Create a workspace and its first app. Needs an empty directory."),
        ("add app <name>", "Add another app to this workspace. One workspace holds many."),
        ("import <app.definition.json> [--app <name>]",
            "Bring an existing App Definition in as source files you can edit."),
        ("check [--app <key>] [--target <id>]",
            "Parse, lower and validate. No model, no database. With --target, also ask whether "
            + "that generator can build it."),
        ("validate [--app <key>] [--target <id>]", "The same command; the word for asking about a target."),
        ("targets", "What this build can generate, and what each target deliberately will not."),
        ("build [--app <key>]", "Write .cordango/build/<key>/ artifacts. Deterministic."),
        ("build --app <key> --target <id> --out <dir>",
            "Generate a whole application into a directory you own. Add --seed <n> for a different "
            + "dataset, --allow-incomplete to accept a build the generator cannot finish, and "
            + "--runtime package to reference Cordango.Standalone from a feed instead of checking "
            + "its source in."),
        ("inspect [path] [--app <key>]", "Describe the workspace, one app, or one aggregate."),
        ("vocabulary [<name>]", "What may be written: Cord's words, or one construct's schema."),
        ("apply <ops.json> --app <key> --scope <kind[:key]>",
            "Apply semantic operations and rewrite the affected source files."),
        ("fmt [--app <key>]", "Rewrite every .cordango.yaml file in canonical form."),
        ("doctor", "Check the workspace for problems that are not source errors."),
        ("version", "CLI, source-format and App Definition schema versions."),
        // The connected half. Listed apart in the human rendering below, because everything above
        // works with no instance, no account and no network, and that is worth seeing at a glance.
        ("login <token> [--instance <url>]", "Connect to a Cordango instance. Make a token under your avatar → Personal Access Keys."),
        ("whoami [--offline]", "Which instance this workspace publishes to, and as whom."),
        ("publish [--app <key>] [--force]", "Build from source, send it to the instance, and make it live."),
        ("logout [--instance <url>] [--all]", "Forget a stored credential on this machine. Does not revoke it."),
    ];

    /// <summary>Where the offline list ends and the connected list begins.</summary>
    private const int FirstConnectedCommand = 13;

    public static int Print(Output output) => output.Ok(
        new JsonObject
        {
            ["commands"] = new JsonArray([.. Commands.Select(c => (JsonNode)new JsonObject
            {
                ["usage"] = c.Usage,
                ["description"] = c.What,
                ["connected"] = Array.IndexOf(Commands, c) >= FirstConnectedCommand,
            })]),
        },
        w =>
        {
            w.WriteLine("cordango — author Cordango apps as semantic source.");
            w.WriteLine();
            for (var i = 0; i < Commands.Length; i++)
            {
                if (i == FirstConnectedCommand)
                {
                    w.WriteLine();
                    w.WriteLine("  Connected to an instance:");
                    w.WriteLine();
                }
                w.WriteLine($"  cordango {Commands[i].Usage}");
                w.WriteLine($"      {Commands[i].What}");
            }
            w.WriteLine();
            w.WriteLine("  Every command accepts --json.");
        });
}

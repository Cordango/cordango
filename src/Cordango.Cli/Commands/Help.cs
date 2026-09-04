// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Cli.Commands;

public static class Help
{
    /// <summary>One command in the list.</summary>
    /// <param name="Connected">Whether it needs an instance. A flag on the row rather than an index
    /// into the array: the divider used to be a constant that had to be moved by hand whenever a
    /// command was added, which is the kind of bookkeeping that is wrong once and then wrong
    /// forever.</param>
    private sealed record Command(string Usage, string What, bool Connected = false);

    /// <summary>Commands this build actually has. `cordango help --json` is how an agent discovers the
    /// surface without a human pasting a README into its context.</summary>
    private static readonly Command[] Commands =
    [
        new("new <first-app>", "Create a workspace and its first app. Needs an empty directory."),
        new("add app <name>", "Add another app to this workspace. One workspace holds many."),
        new("configure [--target <id>]",
            "Decide once where this workspace's apps are meant to run, and write it into "
            + "cordango.yaml. Asks, when there is somebody to ask. `--show` prints what is set."),
        new("import <app.definition.json> [--app <name>]",
            "Bring an App Definition on disk in as source files you can edit. No connection needed."),
        new("check [--app <key>] [--target <id>]",
            "Parse, lower and validate. No model, no database. With --target, also ask whether "
            + "that generator can build it."),
        new("validate [--app <key>] [--target <id>]", "The same command; the word for asking about a target."),
        new("targets", "What this build can generate, and what each target deliberately will not."),
        new("build [--app <key>]",
            "Do what `cordango configure` said: write .cordango/build/<key>/ artifacts, and, when a "
            + "target is configured, generate each application into generated/<key>/. Deterministic."),
        new("build --target <id> [--app <key>]",
            "Generate with a target this once, whatever the configuration says. Add --seed <n> for "
            + "a different dataset, --allow-incomplete to accept a build the generator cannot "
            + "finish, and --runtime source to check the Cordango.Standalone source in as a sibling "
            + "project instead of restoring the package."),
        new("inspect [path] [--app <key>]", "Describe the workspace, one app, or one aggregate."),
        new("discover [<query>] [--app <key>] [--entities|--events|--actions|--rules] [--limit <n>]",
            "What already exists: every app's entities, the events it announces, the actions it "
            + "offers and the rules they carry. Run it BEFORE modelling — if something is already "
            + "there, link to it instead of declaring it again. Works offline; a login adds the "
            + "rest of the instance's apps."),
        new("vocabulary [<name>]", "What may be written: Cord's words, or one construct's schema."),
        new("apply <ops.json> --app <key> --scope <kind[:key]>",
            "Apply semantic operations and rewrite the affected source files."),
        new("fmt [--app <key>]", "Rewrite every .cordango.yaml file in canonical form."),
        new("doctor", "Check the workspace for problems that are not source errors."),
        new("version", "CLI, source-format and App Definition schema versions."),
        // The connected half. Listed apart in the human rendering below, because everything above
        // works with no instance, no account and no network, and that is worth seeing at a glance.
        new("login <token> [--instance <url>]", "Connect to a Cordango instance. Make a token under your avatar → Personal Access Keys.", true),
        new("whoami [--offline]", "Which instance this workspace publishes to, and as whom.", true),
        new("publish [--app <key>] [--force]", "Build from source, send it to the instance, and make it live.", true),
        new("import [<app>] [--list]",
            "The way back: bring one of the instance's apps in as source. With no name it lists what "
            + "you can reach and asks which one. `--list` only looks.", true),
        new("logout [--instance <url>] [--all]", "Forget a stored credential on this machine. Does not revoke it.", true),
    ];

    public static int Print(Output output) => output.Ok(
        new JsonObject
        {
            ["commands"] = new JsonArray([.. Commands.Select(c => (JsonNode)new JsonObject
            {
                ["usage"] = c.Usage,
                ["description"] = c.What,
                ["connected"] = c.Connected,
            })]),
        },
        w =>
        {
            w.WriteLine("cordango — author Cordango apps as semantic source.");
            w.WriteLine();

            var connected = false;
            foreach (var command in Commands)
            {
                if (command.Connected && !connected)
                {
                    connected = true;
                    w.WriteLine();
                    w.WriteLine("  " + Ansi.Bold("Connected to an instance:"));
                    w.WriteLine();
                }

                w.WriteLine("  cordango " + Ansi.Bold(command.Usage));
                foreach (var line in Wrap(command.What, 72)) w.WriteLine("      " + Ansi.Dim(line));
            }

            w.WriteLine();
            w.WriteLine("  Every command accepts --json.");
            w.WriteLine("  Nothing ever prompts under --json, in CI, or with --no-interaction.");
        });

    /// <summary>
    /// Soft-wrap a description at a word boundary.
    ///
    /// <para>Because the descriptions grew past a terminal's width and a hard wrap in the middle of
    /// <c>Cordango.Stand|alone</c> reads as a typo in the product rather than in the help text.</para>
    /// </summary>
    private static IEnumerable<string> Wrap(string text, int width)
    {
        var line = new System.Text.StringBuilder();

        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                yield return line.ToString();
                line.Clear();
            }

            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }

        if (line.Length > 0) yield return line.ToString();
    }
}

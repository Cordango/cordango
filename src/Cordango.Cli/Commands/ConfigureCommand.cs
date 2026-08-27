// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Cli.Remote;
using Cordango.Cli.Workspace;

namespace Cordango.Cli.Commands;

/// <summary>
/// Decide once what <c>cordango build</c> does, and write it into <c>cordango.yaml</c>.
///
/// <para><b>Two ways in, and they produce the same file.</b> Flags, for a script and for anybody who
/// already knows the answer; a single question otherwise. ONE question is a constraint rather than
/// an accident — everything else has a default that is right for almost everybody, and a wizard that
/// asks about the seed value teaches people to stop reading the questions.</para>
///
/// <para><b>The platform target is refused without a connection.</b> Publishing to an instance is
/// not a thing you can do alone: the apps there reference each other, the core apps are the
/// instance's, and the definition is validated by the server rather than by this binary. Writing
/// <c>target: platform</c> into a workspace that cannot reach one would be recording an intention as
/// a configuration, and the first person to run <c>cordango build</c> would find out instead.</para>
/// </summary>
public static class ConfigureCommand
{
    public static int Run(Args args, Output output)
    {
        if (RetiredOut(args, output) is { } retired) return retired;

        var workspace = WorkspaceFile.Find(Directory.GetCurrentDirectory(), out var problem);
        if (workspace is null)
        {
            return output.Fail(
                problem ?? $"no {WorkspaceFile.FileName} in this directory or any parent",
                problem is null ? ["Run `cordango new <app-name>` in an empty directory to create one."] : [],
                code: ExitCodes.NoWorkspace);
        }

        if (args.Has("show")) return Show(workspace, output);

        var config = FromFlags(args, workspace.Build, out var usage);
        if (usage is not null) return output.Usage(usage);

        if (config is null)
        {
            if (Interview.Open(args) is not { } interview)
            {
                // Nothing to change, nobody to ask, and an answer already on file: the question
                // "what is configured here" is the only one left, so answer that instead of
                // refusing. An agent runs this exact command to find out.
                if (workspace.Build is not null) return Show(workspace, output);

                return output.Fail("this workspace has no build configuration and there is nobody to ask",
                [
                    "Name the target, for example:",
                    "  cordango configure --target standalone",
                    "  cordango configure --target platform",
                ], code: ExitCodes.Usage);
            }

            config = Ask(interview, workspace.Build);
        }

        return Write(args, workspace, config, output);
    }

    /// <summary>
    /// Validate, connect if the target says so, and save.
    ///
    /// <para>Shared with <c>build</c>, which runs the same interview on a workspace nobody has
    /// configured yet — so the checks a configuration passes cannot depend on which command asked
    /// the questions.</para>
    /// </summary>
    public static int Write(Args args, WorkspaceFile workspace, BuildConfig config, Output output)
    {
        if (config.Problems() is { Count: > 0 } problems)
            return output.Fail("that is not a configuration this build understands", problems,
                code: ExitCodes.Usage);

        if (config.IsPlatform && !RequireConnection(args, workspace, output, out var exit)) return exit;

        var saved = workspace with { Build = config };
        saved.Save();

        return output.Ok(
            new JsonObject
            {
                ["workspace"] = saved.Name,
                ["path"] = saved.Path,
                ["build"] = config.ToDocument(),
            },
            w =>
            {
                w.WriteLine($"Wrote the build configuration to {WorkspaceFile.FileName}:");
                w.WriteLine();
                Describe(w, config);
                w.WriteLine();
                w.WriteLine("  cordango build" + Ansi.Dim("     # no flags needed from here"));
            });
    }

    /// <summary>
    /// A connection is a REQUIREMENT of the platform target, not a nicety.
    ///
    /// <para>Deliberately the stored-credential check and not a call to the server: <c>configure</c>
    /// and <c>build</c> are offline commands and must stay that way. What is being refused here is
    /// "I have never connected this workspace to anything", which is the case that actually
    /// happens.</para>
    /// </summary>
    public static bool RequireConnection(Args args, WorkspaceFile workspace, Output output, out int exit)
    {
        if (Connection.Resolve(args, workspace, output, out exit) is not null) return true;

        // Connection.Resolve has already written why. Add the sentence that explains why THIS
        // command cares, which is the part somebody meeting the refusal is missing.
        output.Note("");
        output.Note("The platform target publishes to an instance: it needs to see the other apps "
            + "there, the core apps, and the workspace it is publishing into. Connect first, or "
            + "choose the standalone target with `cordango configure --target standalone`.");
        return false;
    }

    /// <summary>
    /// The interview. One question, because there is only one thing this cannot decide for itself.
    /// </summary>
    public static BuildConfig Ask(Interview interview, BuildConfig? existing)
    {
        var current = existing ?? BuildConfig.Default;

        interview.Say();
        interview.Say(Ansi.Bold("Let's set up how this workspace builds.")
            + " " + Ansi.Dim("One question, then `cordango build` works on its own."));
        interview.Say();

        var target = interview.Choose("Where do these apps run?",
        [
            (BuildConfig.Standalone,
                $"A repository you own, generated into {BuildConfig.GeneratedDirectory}/. Deploy it anywhere."),
            (BuildConfig.Platform,
                "A Cordango instance you publish to. Needs a connection."),
        ], current.Target);

        return current with { Target = target };
    }

    /// <summary>
    /// <c>--out</c> used to be how you said where a generated application went, and it is gone.
    ///
    /// <para>Refused rather than ignored. An unknown flag on this parser is silently dropped, so a
    /// script that still passes <c>--out ../somewhere</c> would keep exiting zero while writing
    /// somewhere else entirely — the one failure mode worse than the flag itself.</para>
    /// </summary>
    public static int? RetiredOut(Args args, Output output) => args.Has("out")
        ? output.Fail("--out is gone",
        [
            $"An application is generated into {BuildConfig.GeneratedDirectory}/<app>/ inside the "
            + "workspace, and there is nothing to decide.",
            "It is still yours and it still leaves: move the directory, or give it a remote of its own.",
        ], code: ExitCodes.Usage)
        : null;

    /// <summary>
    /// The flag spelling of the same configuration.
    /// </summary>
    /// <returns>Null when no flag said anything — the caller then asks, or refuses.</returns>
    private static BuildConfig? FromFlags(Args args, BuildConfig? existing, out string? usage)
    {
        usage = null;

        var named = args.Value("target") ?? (args.First is { Length: > 0 } first ? first : null);
        var runtime = args.Value("runtime");
        var seed = args.Value("seed");
        var incomplete = args.Has("allow-incomplete") || args.Has("allow-partial-ui");

        if (named is null && runtime is null && seed is null && !incomplete)
            return null;

        var config = existing ?? BuildConfig.Default;

        if (seed is not null && !int.TryParse(seed, out _))
        {
            usage = $"--seed {seed} is not a number";
            return null;
        }

        return config with
        {
            Target = named ?? config.Target,
            Runtime = runtime ?? config.Runtime,
            AllowIncomplete = incomplete || config.AllowIncomplete,
            Seed = seed is not null ? int.Parse(seed) : config.Seed,
        };
    }

    private static int Show(WorkspaceFile workspace, Output output)
    {
        if (workspace.Build is not { } config)
        {
            return output.Ok(
                new JsonObject { ["workspace"] = workspace.Name, ["build"] = null },
                w =>
                {
                    w.WriteLine($"{workspace.Name} has no build configuration.");
                    w.WriteLine();
                    w.WriteLine("  cordango configure" + Ansi.Dim("     # two questions"));
                });
        }

        return output.Ok(
            new JsonObject
            {
                ["workspace"] = workspace.Name,
                ["path"] = workspace.Path,
                ["build"] = config.ToDocument(),
            },
            w => Describe(w, config));
    }

    /// <summary>The configuration in the words the questions used, not the words the file uses.</summary>
    private static void Describe(TextWriter w, BuildConfig config)
    {
        if (config.IsPlatform)
        {
            w.WriteLine($"  target    {config.Target}   {Ansi.Dim("published to the connected instance")}");
            return;
        }

        w.WriteLine($"  target    {config.Target}");
        w.WriteLine($"  out       {BuildConfig.GeneratedDirectory}/<app>/   "
            + Ansi.Dim("not a setting — this is where generated applications go"));
        w.WriteLine($"  runtime   {config.Runtime}   "
            + Ansi.Dim(config.Runtime == BuildConfig.RuntimeSource
                ? "Cordango.Standalone checked in as a sibling project"
                : "Cordango.Standalone restored from a feed"));
        if (config.AllowIncomplete)
            w.WriteLine("  " + Ansi.Bold("allowIncomplete true")
                + "   " + Ansi.Dim("gaps are listed in the generated README, not refused"));
        w.WriteLine($"  seed      {config.Seed}");
    }
}

// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Cli.Workspace;

/// <summary>
/// The <c>build:</c> block of <c>cordango.yaml</c>: where this workspace's apps are meant to end up.
///
/// <para><b>It exists so that <c>cordango build</c> takes no flags.</b> Every generated application
/// is the product of the same decisions — which target, with the runtime restored or vendored, and
/// whether an unfinished build is acceptable — and before this block they had to be retyped on every
/// invocation, by every person, in every script. A decision retyped is a decision that eventually
/// differs, and two developers generating the same workspace two ways is exactly the drift the
/// deterministic compiler exists to prevent.</para>
///
/// <para><b>Where the output goes is not one of those decisions.</b> It is
/// <see cref="GeneratedDirectory"/><c>/&lt;app-key&gt;/</c> inside the workspace, always. A
/// caller-chosen directory was a setting whose every wrong answer is a real problem — an existing
/// project the writer must refuse to merge into, a relative path that means two places depending on
/// where you stood, a second developer generating somewhere the first one did not look. A generated
/// application is still yours and still leaves: move the directory, or point a remote at it. What is
/// gone is having to say so before anything can be built.</para>
///
/// <para><b>It is committed, unlike the credential.</b> This says what the workspace IS, which is the
/// same answer for everybody who checks it out. Where it publishes and as whom is per person and per
/// machine and lives in <see cref="Remote.Credentials"/> — see the note there.</para>
///
/// <para><b>Absent is a real state.</b> A workspace with no <c>build:</c> block is not
/// misconfigured; it has not been asked yet. <c>cordango build</c> asks when there is somebody to
/// ask, and otherwise does the offline half of its job and says what it did not do.</para>
/// </summary>
/// <param name="Target">A generator id or alias — see <c>cordango targets</c> — or
/// <see cref="Platform"/>.</param>
/// <param name="Runtime">Restore <c>Cordango.Standalone</c> as a package, or check its source in as
/// a sibling project. <c>package</c> or <c>source</c>.</param>
/// <param name="AllowIncomplete">Accept a build the target cannot finish. False by default and
/// written into the file when true, because a partial build that nobody can see the permission for
/// is how a gap becomes permanent.</param>
/// <param name="Seed">The dataset seed. Same seed, same demo data, on every machine.</param>
public sealed record BuildConfig(
    string Target,
    string Runtime,
    bool AllowIncomplete,
    int Seed)
{
    /// <summary>The key this block sits under in <c>cordango.yaml</c>.</summary>
    public const string Key = "build";

    /// <summary>
    /// The target that is not a generator: a Cordango instance the workspace publishes to.
    ///
    /// <para>It is named here rather than in <see cref="Generate.Targets"/> because it produces no
    /// source. Everything a generator does — capability refusals, emitted files, a repository you
    /// own — has no meaning for it, and the platform runs the whole language rather than a certified
    /// subset of it. What it needs instead is a connection, which is why every command that acts on
    /// this value checks for one first.</para>
    /// </summary>
    public const string Platform = "platform";

    /// <summary>The word for "a repository you own", aliased to whichever generator is the default
    /// one. Stated in <see cref="Generate.Targets"/>; repeated here only as the answer a fresh
    /// interview offers first.</summary>
    public const string Standalone = "standalone";

    public const string RuntimePackage = "package";
    public const string RuntimeSource = "source";

    /// <summary>Where generated applications land, relative to the workspace root. Gitignored by the
    /// scaffold: it is build output, reproducible from the source beside it.</summary>
    public const string GeneratedDirectory = "generated";

    public const int DefaultSeed = 42;

    public static readonly BuildConfig Default =
        new(Standalone, RuntimePackage, AllowIncomplete: false, DefaultSeed);

    public bool IsPlatform => string.Equals(Target, Platform, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The generated repository, relative to the workspace root — which is
    /// <see cref="GeneratedDirectory"/> itself, with nothing under it.
    /// </summary>
    /// <remarks>
    /// <para>It was one directory per APP. That was right while a build produced one application per
    /// deployment, and wrong the moment it stopped: the apps in a workspace share a database, a
    /// sign-in, a directory and a shell, so they are ONE repository. The per-app layer existed only
    /// to keep several repositories apart, and with nothing to keep apart it is a directory whose
    /// name has to be derived from something — which is how it becomes a decision, and then a
    /// setting.</para>
    /// <para>So there is no name to derive. A workspace builds into <c>generated/</c> and
    /// <c>cd generated &amp;&amp; docker compose up --build</c> runs it, which is the line the README
    /// has always printed. The compose PROJECT name is set inside the file from the workspace key,
    /// so two workspaces on one machine still get their own containers and their own volume.</para>
    /// </remarks>
    public static string OutFor(WorkspaceFile workspace)
    {
        System.ArgumentNullException.ThrowIfNull(workspace);
        return GeneratedDirectory;
    }

    /// <summary>
    /// Read the block, or null when there is none.
    ///
    /// <para>A missing key inside a block that DOES exist takes the default rather than failing:
    /// somebody hand-writing a two-line <c>build:</c> has said the thing that matters, and demanding
    /// the rest would make the file harder to write than the flags it replaces.</para>
    /// </summary>
    public static BuildConfig? Read(JsonNode? node)
    {
        if (node is not JsonObject block) return null;

        var seed = block["seed"] is JsonValue value && value.TryGetValue<long>(out var declared)
            ? (int)declared
            : DefaultSeed;

        return new BuildConfig(
            (string?)block["target"] is { Length: > 0 } target ? target : Standalone,
            (string?)block["runtime"] is { Length: > 0 } runtime ? runtime : RuntimePackage,
            (bool?)block["allowIncomplete"] ?? false,
            seed);
    }

    /// <summary>
    /// The block as it is written back.
    ///
    /// <para>A platform workspace writes ONE key. The other three describe generating source into a
    /// directory, which publishing does not do, and a file that answered questions nobody asked
    /// would read as though the seed were doing something.</para>
    /// </summary>
    public JsonObject ToDocument() => IsPlatform
        ? new JsonObject { ["target"] = Target }
        : new JsonObject
        {
            ["target"] = Target,
            ["runtime"] = Runtime,
            ["allowIncomplete"] = AllowIncomplete,
            ["seed"] = Seed,
        };

    /// <summary>What is wrong with this configuration, in the words the CLI would use. Empty when
    /// nothing is.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();

        if (!IsPlatform && Generate.Targets.Find(Target) is null)
            problems.Add($"no target called '{Target}' — known targets: "
                + $"{Generate.Targets.Known}, {Platform}");

        if (!IsPlatform && Runtime is not (RuntimePackage or RuntimeSource))
            problems.Add($"runtime: '{Runtime}' — it is either '{RuntimePackage}' or '{RuntimeSource}'");

        return problems;
    }
}

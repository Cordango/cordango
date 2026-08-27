// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace Cordango.Cli;

/// <summary>
/// Stable exit codes. Documented because agents and CI branch on them, and a code that shifts
/// meaning between releases is worse than no code at all.
/// </summary>
public static class ExitCodes
{
    public const int Ok = 0;

    /// <summary>The command ran and the answer was no: check found errors, apply was refused,
    /// doctor found a problem. The tool worked; the workspace did not.</summary>
    public const int Failed = 1;

    /// <summary>The command line was wrong — unknown command, missing argument, bad flag. Separated
    /// from <see cref="Failed"/> so an agent can tell "I called it wrong" from "it said no".</summary>
    public const int Usage = 2;

    /// <summary>No <c>cordango.yaml</c> above the working directory, or the one found is unreadable.</summary>
    public const int NoWorkspace = 3;

    /// <summary>The command needed a Cordango instance and could not use one: not logged in, the
    /// credential was rejected, or the address did not answer. Separated from <see cref="Failed"/>
    /// so a CI job can retry a flaky network without retrying a genuinely broken definition.</summary>
    public const int NoInstance = 4;
}

/// <summary>
/// Parsed argv: the verb, its positional arguments, and its flags.
///
/// <para>Hand-rolled rather than a parser package, and that is a decision worth stating: the flag
/// surface here is nine commands with at most three options each, all of them documented in
/// <c>Help</c>. A dependency would buy completion scripts and cost the single-file publish an
/// assembly. Revisit when the surface grows, not before.</para>
/// </summary>
public sealed class Args
{
    private readonly List<string> _positional = [];
    private readonly Dictionary<string, string?> _flags = new(StringComparer.Ordinal);

    private Args() { }

    /// <summary>The verb, plus any sub-verb: <c>add app</c> is one command, not a command with an
    /// argument, because <c>cordango add app crm</c> and <c>cordango add entity claim</c> scaffold entirely
    /// different things.</summary>
    public string Command { get; private set; } = "";

    public IReadOnlyList<string> Positional => _positional;

    /// <summary>Every command supports it (CordyOSS §5), so it is read here rather than per command.
    /// It also suppresses every prompt — see <see cref="Interview"/>.</summary>
    public bool Json => Has("json");

    public bool Has(string flag) => _flags.ContainsKey(flag);

    public string? Value(string flag) => _flags.GetValueOrDefault(flag);

    public string? First => _positional.Count > 0 ? _positional[0] : null;

    public static Args Parse(string[] argv)
    {
        var args = new Args();
        var i = 0;

        if (i < argv.Length && !argv[i].StartsWith('-'))
        {
            args.Command = argv[i++];

            // Two-word commands. Kept to an explicit list so `cordango check app` cannot silently become
            // a command nobody wrote — an unknown pair falls through and `check` sees `app` as a
            // positional, which the command itself then rejects by name.
            if (i < argv.Length && !argv[i].StartsWith('-')
                && (args.Command, argv[i]) is ("add", "app") or ("app", "remove"))
                args.Command += " " + argv[i++];
        }

        for (; i < argv.Length; i++)
        {
            var arg = argv[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                args._positional.Add(arg);
                continue;
            }

            var name = arg[2..];
            var eq = name.IndexOf('=');
            if (eq >= 0)
            {
                args._flags[name[..eq]] = name[(eq + 1)..];
                continue;
            }

            // `--app expenses` — a following non-flag token is this flag's value. `--json` takes
            // none, so the lookahead has to stop at the next `--`.
            if (i + 1 < argv.Length && !argv[i + 1].StartsWith("--", StringComparison.Ordinal)
                && name is "app" or "scope" or "ops" or "out" or "instance" or "token"
                    or "target" or "seed" or "seed-date" or "runtime")
                args._flags[name] = argv[++i];
            else
                args._flags[name] = null;
        }

        return args;
    }
}

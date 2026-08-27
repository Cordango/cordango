// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace Cordango.Cli;

/// <summary>
/// Colour, when there is somebody to see it.
///
/// <para><b>Off in every case that is not a terminal.</b> Escape codes in a redirected stream end up
/// in log files, in CI annotations and in whatever an agent parsed the output with, and they are
/// invisible until they are not. Three separate signals turn it off and any one of them is enough:
/// <c>NO_COLOR</c> (the convention), <c>TERM=dumb</c>, and a redirected stdout.</para>
/// </summary>
public static class Ansi
{
    /// <summary>Settable so the tests can pin it — an assertion on human output must not depend on
    /// whether the test host happened to own a console.</summary>
    public static bool Enabled { get; internal set; } = Compute();

    private static bool Compute()
    {
        if (Environment.GetEnvironmentVariable("NO_COLOR") is { Length: > 0 }) return false;
        if (Environment.GetEnvironmentVariable("TERM") is "dumb") return false;
        return !Console.IsOutputRedirected;
    }

    private const string Escape = "\u001b";

    private static string Wrap(string code, string text) =>
        Enabled ? Escape + code + text + Escape + "[0m" : text;

    /// <summary>The brand's gold. The one accent colour, used for the mark and for nothing else.</summary>
    public static string Gold(string text) => Wrap("[33m", text);

    public static string Bold(string text) => Wrap("[1m", text);

    /// <summary>Secondary text — the explanation beside a command, the default beside a question.</summary>
    public static string Dim(string text) => Wrap("[2m", text);
}

/// <summary>
/// Dante and the wordmark, in characters.
///
/// <para>Drawn rather than generated, and kept in one place: <c>cordango version</c> and
/// <c>cordango new</c> are the two moments where the tool gets to look like a product rather than a
/// build step, and a logo assembled separately in two commands is a logo with two versions.</para>
///
/// <para>Half blocks, no braille, and no geometric shapes beyond <c>●</c> and <c>○</c>.
/// <see cref="Console.OutputEncoding"/> is already UTF-8 before anything writes — a German label in
/// an error message needs that as much as this does — so the constraint here is FONT coverage rather
/// than encoding, and the block-drawing range is the part every terminal font has.</para>
/// </summary>
public static class Branding
{
    /// <summary>Dante: the head with its eye, and the track underneath.</summary>
    private static readonly string[] Mark =
    [
        "  ▄▄▄▄▄▄▄",
        " ▐██████▛▀▜▖",
        " ▐██████ ● ▌",
        "  ▀▀▀▀▀▀▀▀▀",
        " ▄▄▄▄▄▄▄▄▄▄▄",
        "▐ ○ ▄▄▄▄▄▄▄ ▌",
        " ▀▀▀▀▀▀▀▀▀▀▀",
    ];

    private static readonly string[] Wordmark =
    [
        "████ ████ ███  ███  ████ █  █ ████ ████",
        "█    █  █ █  █ █  █ █  █ ██ █ █    █  █",
        "█    █  █ ███  █  █ ████ █ ██ █ ██ █  █",
        "█    █  █ █  █ █  █ █  █ █  █ █  █ █  █",
        "████ ████ █  █ ███  █  █ █  █ ████ ████",
    ];

    /// <summary>
    /// The banner, with the wordmark beside the mark rather than under it.
    ///
    /// <para>Fifty-six columns at its widest, so it survives an eighty-column terminal without
    /// wrapping into nonsense — a logo that wraps is worse than no logo.</para>
    /// </summary>
    public static void Write(TextWriter writer)
    {
        var width = Mark.Max(line => line.Length);

        writer.WriteLine();
        for (var row = 0; row < Mark.Length; row++)
        {
            var word = row is > 0 and <= 5 ? Wordmark[row - 1] : "";
            var line = "  " + Ansi.Gold(Mark[row].PadRight(width))
                + (word.Length > 0 ? "   " + Ansi.Bold(word) : "");
            writer.WriteLine(line.TrimEnd());
        }
        writer.WriteLine();
    }
}

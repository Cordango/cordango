// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace Cordango.Cli;

/// <summary>
/// Asking a person a question, and the rules about when that is allowed.
///
/// <para><b>A question is a failure mode, not a feature.</b> Every command in this tool has to work
/// in a pipeline, in CI, and inside an agent loop where nobody is watching the terminal — so an
/// interactive prompt is only ever offered when all four of these hold: output is not
/// <c>--json</c>, the caller did not say <c>--no-interaction</c>, stdin and stdout are both a
/// console, and neither <c>CI</c> nor <c>CORDANGO_NO_INTERACTION</c> is set. When any one fails the
/// command must do the whole job from its flags and its defaults, and say what it assumed.</para>
///
/// <para>A prompt writes to stdout rather than stderr, which is safe precisely because the check
/// above has already ruled out every case where stdout is somebody's data.</para>
/// </summary>
public sealed class Interview
{
    private readonly TextReader _input;
    private readonly TextWriter _output;

    private Interview(TextReader input, TextWriter output)
    {
        _input = input;
        _output = output;
    }

    /// <summary>Answers scripted by a test. Set, it also stands in for "there is a console" — the
    /// alternative is a test that passes or fails depending on how the test host was launched.</summary>
    internal static TextReader? Scripted { get; set; }

    /// <summary>The environment variable that turns every prompt in this tool off. Named in the
    /// message a non-interactive command prints, so that turning it off is discoverable from the
    /// output rather than from the source.</summary>
    public const string SilenceVariable = "CORDANGO_NO_INTERACTION";

    /// <returns>An interview, or null when there is nobody to ask. Null is not an error — it is the
    /// caller's cue to use its defaults and say so.</returns>
    public static Interview? Open(Args args)
    {
        if (args.Json || args.Has("no-interaction") || args.Has("yes")) return null;
        if (Scripted is not null) return new Interview(Scripted, Console.Out);

        if (Environment.GetEnvironmentVariable(SilenceVariable) is { Length: > 0 }) return null;
        if (Environment.GetEnvironmentVariable("CI") is { Length: > 0 }) return null;
        if (Console.IsInputRedirected || Console.IsOutputRedirected) return null;

        return new Interview(Console.In, Console.Out);
    }

    public void Say(string line = "") => _output.WriteLine(line);

    /// <summary>
    /// One question with a default. Enter takes the default, which is printed — a prompt whose
    /// default is invisible is a prompt people answer wrongly.
    /// </summary>
    public string Ask(string question, string fallback)
    {
        _output.WriteLine(question);
        _output.Write($"  > {Ansi.Dim($"[{fallback}] ")}");
        _output.Flush();

        var answer = _input.ReadLine();
        _output.WriteLine();

        // End of input mid-interview: take the default rather than looping forever on a stream that
        // will never produce another line.
        return string.IsNullOrWhiteSpace(answer) ? fallback : answer.Trim();
    }

    /// <summary>
    /// A choice between named options, each with the sentence that explains it.
    ///
    /// <para>Both spellings are accepted: the number, and the option's own name. Somebody who knows
    /// they want <c>platform</c> should be able to type it, and somebody reading the list for the
    /// first time should be able to press 1.</para>
    /// </summary>
    public string Choose(string question, IReadOnlyList<(string Name, string What)> options, string fallback)
    {
        // Column width from the longest name, not a constant: a fixed twelve looked fine for
        // `standalone` and ran `task-manager` straight into its own description.
        var width = options.Max(o => o.Name.Length) + 3;

        _output.WriteLine(question);
        _output.WriteLine();
        for (var i = 0; i < options.Count; i++)
            _output.WriteLine($"    {i + 1}  {Ansi.Bold(options[i].Name.PadRight(width))}{Ansi.Dim(options[i].What)}");
        _output.WriteLine();

        while (true)
        {
            _output.Write($"  > {Ansi.Dim($"[{fallback}] ")}");
            _output.Flush();

            var answer = _input.ReadLine();
            if (string.IsNullOrWhiteSpace(answer))
            {
                _output.WriteLine();
                return fallback;
            }

            answer = answer.Trim();

            if (int.TryParse(answer, out var index) && index >= 1 && index <= options.Count)
            {
                _output.WriteLine();
                return options[index - 1].Name;
            }

            var named = options.FirstOrDefault(o =>
                string.Equals(o.Name, answer, StringComparison.OrdinalIgnoreCase));
            if (named.Name is { Length: > 0 })
            {
                _output.WriteLine();
                return named.Name;
            }

            _output.WriteLine($"    {answer} is not one of them — {string.Join(", ", options.Select(o => o.Name))}");
        }
    }
}

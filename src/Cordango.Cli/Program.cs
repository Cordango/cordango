// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.Cli;
using Cordango.Cli.Commands;

// Console output is UTF-8 or German labels come back as question marks on Windows. Set before
// anything writes, including a help text nobody thought carried a non-ASCII character.
Console.OutputEncoding = System.Text.Encoding.UTF8;

// `args` is the implicit parameter of a top-level program — argv without the executable.
var parsed = Args.Parse(args);
var output = new Output(parsed.Json);

// Ctrl+C during a publish should abandon the HTTP call, not kill the process mid-write. The
// offline commands ignore it because they finish faster than a keystroke.
using var interrupt = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    interrupt.Cancel();
};

try
{
    return parsed.Command switch
    {
        "new" => NewCommand.Run(parsed, output),
        "add app" => AddAppCommand.Run(parsed, output),
        // Offline like everything around it, with one exception it states out loud: the platform
        // target will not be written into a workspace that has never connected to an instance.
        "configure" or "config" => ConfigureCommand.Run(parsed, output),
        "check" => CheckCommand.Run(parsed, output),
        // `validate` is the same command under the word people reach for when they are asking about
        // a TARGET rather than about their source. One implementation, two spellings, because two
        // implementations of "is this all right" is how the two answers start disagreeing.
        "validate" => CheckCommand.Run(parsed, output),
        "targets" => TargetsCommand.Run(parsed, output),
        // Offline for a file, connected for everything else — see the note on the command.
        "import" => await ImportCommand.RunAsync(parsed, output, interrupt.Token),
        "build" => BuildCommand.Run(parsed, output),
        "inspect" => InspectCommand.Run(parsed, output),
        // Offline for the workspace and the core apps, richer when connected — the one command whose
        // answer legitimately grows with a login rather than requiring one.
        "discover" => await DiscoverCommand.RunAsync(parsed, output, interrupt.Token),
        "vocabulary" or "vocab" => VocabularyCommand.Run(parsed, output),
        "apply" => ApplyCommand.Run(parsed, output),
        "fmt" => FmtCommand.Run(parsed, output),
        "doctor" => DoctorCommand.Run(parsed, output),
        // The only commands that touch a network. Everything above is offline and token-free, and
        // that separation is a release gate rather than a coincidence (CordyOSS §14).
        "login" => await LoginCommand.RunAsync(parsed, output, interrupt.Token),
        "logout" => LogoutCommand.Run(parsed, output),
        "whoami" => await WhoamiCommand.RunAsync(parsed, output, interrupt.Token),
        "publish" => await PublishCommand.RunAsync(parsed, output, interrupt.Token),
        "version" or "--version" or "-v" => VersionCommand.Run(output),
        "help" or "--help" or "-h" or "" => Help.Print(output),
        var unknown => output.Usage($"unknown command '{unknown}' — run `cordango help`"),
    };
}
catch (OperationCanceledException) when (interrupt.IsCancellationRequested)
{
    return output.Fail("interrupted", []);
}
catch (Exception ex)
{
    // A crash is not a refusal. Reported through the same channel so `--json` callers never get a
    // stack trace where they expected a document, and the trace still reaches stderr for a human.
    if (!parsed.Json) Console.Error.WriteLine(ex);
    return output.Fail("cordango failed unexpectedly", [$"{ex.GetType().Name}: {ex.Message}"]);
}

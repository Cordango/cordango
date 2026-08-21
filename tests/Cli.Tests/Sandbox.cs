// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Cli;

// The commands read the CURRENT DIRECTORY — that is how `cordango check` works from anywhere inside a
// workspace, and it is process-global state. Parallel tests would chdir out from under each other,
// producing failures that move around between runs and never reproduce.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Cordango.Cli.Tests;

/// <summary>
/// A real workspace in a temporary directory, with the process pointed at it and the console
/// captured — so a test drives the same entry point a user does rather than a rearranged copy of it.
/// </summary>
public sealed class Sandbox : IDisposable
{
    private readonly string _previousDirectory = Directory.GetCurrentDirectory();
    private readonly TextWriter _previousOut = Console.Out;
    private readonly TextWriter _previousError = Console.Error;

    private readonly string? _previousConfigDir =
        Environment.GetEnvironmentVariable(Remote.Credentials.DirectoryVariable);

    public Sandbox()
    {
        Root = Path.Combine(Path.GetTempPath(), "cordango-tests", Path.GetRandomFileName());
        Directory.CreateDirectory(Root);
        Directory.SetCurrentDirectory(Root);

        // Credentials go inside the sandbox, never into the developer's real profile. Without this
        // a `logout --all` in a test would sign the person running it out of their own instances.
        ConfigDirectory = Path.Combine(Root, ".cordango-config");
        Environment.SetEnvironmentVariable(Remote.Credentials.DirectoryVariable, ConfigDirectory);
    }

    public string Root { get; }

    /// <summary>Where this sandbox's <c>credentials.json</c> lives.</summary>
    public string ConfigDirectory { get; }

    /// <summary>Everything the last command wrote to stdout.</summary>
    public string Out { get; private set; } = "";

    /// <summary>Everything it wrote to stderr — where human-readable failures go.</summary>
    public string Error { get; private set; } = "";

    /// <summary>Runs a command exactly as <c>Program</c> would, and captures both streams.</summary>
    public int Run(params string[] argv)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);

        try
        {
            var args = Args.Parse(argv);
            var output = new Output(args.Json);

            return args.Command switch
            {
                "new" => Commands.NewCommand.Run(args, output),
                "add app" => Commands.AddAppCommand.Run(args, output),
                "check" => Commands.CheckCommand.Run(args, output),
                "validate" => Commands.CheckCommand.Run(args, output),
                "targets" => Commands.TargetsCommand.Run(args, output),
                "import" => Commands.ImportCommand.Run(args, output),
                "build" => Commands.BuildCommand.Run(args, output),
                "inspect" => Commands.InspectCommand.Run(args, output),
                "vocabulary" => Commands.VocabularyCommand.Run(args, output),
                "apply" => Commands.ApplyCommand.Run(args, output),
                "fmt" => Commands.FmtCommand.Run(args, output),
                "doctor" => Commands.DoctorCommand.Run(args, output),
                "version" => Commands.VersionCommand.Run(output),
                "logout" => Commands.LogoutCommand.Run(args, output),
                // The connected commands are async. Only their OFFLINE refusals are exercised here —
                // "not signed in", "no instance bound" — which is everything that happens before the
                // first socket. A test that reached the network would be testing an instance.
                "whoami" => Commands.WhoamiCommand.RunAsync(args, output, default).GetAwaiter().GetResult(),
                "publish" => Commands.PublishCommand.RunAsync(args, output, default).GetAwaiter().GetResult(),
                "login" => Commands.LoginCommand.RunAsync(args, output, default).GetAwaiter().GetResult(),
                var unknown => throw new InvalidOperationException($"the test called '{unknown}'"),
            };
        }
        finally
        {
            Out = stdout.ToString();
            Error = stderr.ToString();
            Console.SetOut(_previousOut);
            Console.SetError(_previousError);
        }
    }

    /// <summary>Runs with <c>--json</c> and parses the envelope — the agent-facing surface.</summary>
    public (int Exit, JsonObject Payload) RunJson(params string[] argv)
    {
        var exit = Run([.. argv, "--json"]);
        return (exit, (JsonObject)JsonNode.Parse(Out)!);
    }

    public string Path_(params string[] parts) => System.IO.Path.Combine([Root, .. parts]);

    /// <summary>Every <c>.cordango</c> file in the workspace, app-relative path → exact bytes. The
    /// comparison behind "a refused change leaves the files byte-identical".</summary>
    public Dictionary<string, string> Snapshot()
    {
        return Directory
            .EnumerateFiles(Root, "*.cordango.yaml", SearchOption.AllDirectories)
            .ToDictionary(
                f => System.IO.Path.GetRelativePath(Root, f).Replace('\\', '/'),
                File.ReadAllText,
                StringComparer.Ordinal);
    }

    public void Write(string relativePath, string content)
    {
        var path = Path_(relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void Dispose()
    {
        Console.SetOut(_previousOut);
        Console.SetError(_previousError);
        Directory.SetCurrentDirectory(_previousDirectory);
        Environment.SetEnvironmentVariable(Remote.Credentials.DirectoryVariable, _previousConfigDir);

        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a green test over.
        }
    }
}

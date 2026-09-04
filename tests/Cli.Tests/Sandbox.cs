// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Cli;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Cordango.Cli.Tests;

public sealed class Sandbox : IDisposable
{
    private readonly string _previousDirectory = Directory.GetCurrentDirectory();
    private readonly TextWriter _previousOut = Console.Out;
    private readonly TextWriter _previousError = Console.Error;

    private readonly string? _previousConfigDir =
        Environment.GetEnvironmentVariable(Remote.Credentials.DirectoryVariable);

    private readonly string? _previousSilence =
        Environment.GetEnvironmentVariable(Interview.SilenceVariable);

    public Sandbox()
    {
        Root = Path.Combine(Path.GetTempPath(), "cordango-tests", Path.GetRandomFileName());
        Directory.CreateDirectory(Root);
        Directory.SetCurrentDirectory(Root);

        ConfigDirectory = Path.Combine(Root, ".cordango-config");
        Environment.SetEnvironmentVariable(Remote.Credentials.DirectoryVariable, ConfigDirectory);

        // A test runs where nobody is watching, which is the state every command has to handle
        // anyway. Pinned rather than inferred: whether the test host owns a console is not something
        // a test's outcome may depend on.
        Environment.SetEnvironmentVariable(Interview.SilenceVariable, "1");
        Interview.Scripted = null;
        Ansi.Enabled = false;
    }

    public string Root { get; }

    public string ConfigDirectory { get; }

    public string Out { get; private set; } = "";

    public string Error { get; private set; } = "";

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
                "configure" => Commands.ConfigureCommand.Run(args, output),
                "check" => Commands.CheckCommand.Run(args, output),
                "validate" => Commands.CheckCommand.Run(args, output),
                "targets" => Commands.TargetsCommand.Run(args, output),
                "import" => Commands.ImportCommand.RunAsync(args, output, default).GetAwaiter().GetResult(),
                "build" => Commands.BuildCommand.Run(args, output),
                "inspect" => Commands.InspectCommand.Run(args, output),
                "discover" => Commands.DiscoverCommand.RunAsync(args, output, default).GetAwaiter().GetResult(),
                "vocabulary" => Commands.VocabularyCommand.Run(args, output),
                "apply" => Commands.ApplyCommand.Run(args, output),
                "fmt" => Commands.FmtCommand.Run(args, output),
                "doctor" => Commands.DoctorCommand.Run(args, output),
                "version" => Commands.VersionCommand.Run(output),
                "help" => Commands.Help.Print(output),
                "logout" => Commands.LogoutCommand.Run(args, output),
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

    public (int Exit, JsonObject Payload) RunJson(params string[] argv)
    {
        var exit = Run([.. argv, "--json"]);
        return (exit, (JsonObject)JsonNode.Parse(Out)!);
    }

    /// <summary>
    /// Run a command with somebody at the keyboard, answering with <paramref name="answers"/>.
    ///
    /// <para>One line per question. An empty line takes the default, which is what pressing Enter
    /// does — so a test can assert the defaults are the ones a person would get.</para>
    /// </summary>
    public int RunAnswering(string answers, params string[] argv)
    {
        Interview.Scripted = new StringReader(answers);
        try
        {
            return Run(argv);
        }
        finally
        {
            Interview.Scripted = null;
        }
    }

    public string Path_(params string[] parts) => System.IO.Path.Combine([Root, .. parts]);

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
        Environment.SetEnvironmentVariable(Interview.SilenceVariable, _previousSilence);
        Interview.Scripted = null;

        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

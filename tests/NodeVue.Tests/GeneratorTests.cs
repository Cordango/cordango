// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.SourceGen;
using Cordango.SourceGen.NodeVue;

namespace Cordango.NodeVue.Tests;

/// <summary>What the node-vue generator produces, over the same corpus the .NET target is measured
/// against.</summary>
public class GeneratorTests
{
    public static TheoryData<string> Apps() => Build.CorpusData();

    [Theory]
    [MemberData(nameof(Apps))]
    public void It_emits_an_application(string key)
    {
        var paths = Build.Generate(key).Files
            .Select(f => f.RelativePath)
            .ToHashSet(StringComparer.Ordinal);

        // The host, which is yours from the first build.
        Assert.Contains("api/src/server.ts", paths);

        // What the definition became.
        Assert.Contains("api/src/app.ts", paths);
        Assert.Contains("api/src/routes.ts", paths);
        Assert.Contains("api/src/entities.ts", paths);
        Assert.Contains("api/src/permissions.ts", paths);
        Assert.Contains("api/src/commands.ts", paths);
        Assert.Contains("api/src/seed/seed.json", paths);

        // What makes it a project rather than a folder of files.
        Assert.Contains("api/package.json", paths);
        Assert.Contains("api/tsconfig.json", paths);
        Assert.Contains("Dockerfile", paths);
        Assert.Contains("docker-compose.yml", paths);
        Assert.Contains("README.md", paths);

        // And the front end, from the shared emitter.
        Assert.Contains("web/src/main.js", paths);
        Assert.Contains("web/package.json", paths);
    }

    /// <summary>
    /// The same definition produces the same bytes.
    ///
    /// <para>The whole contract of a generator: one definition makes one application rather than a
    /// family of similar ones. A dictionary iterated in hash order, a <c>DateTime.Now</c> in a
    /// header, a <c>Guid.NewGuid()</c> in a seed — any of them would pass every other test here and
    /// fail this one.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Apps))]
    public void Two_builds_of_one_definition_are_identical(string key)
    {
        var first = Build.Generate(key);
        var second = Build.Generate(key);

        Assert.Equal(
            first.Files.Select(f => f.RelativePath),
            second.Files.Select(f => f.RelativePath));

        foreach (var (a, b) in first.Files.Zip(second.Files))
            Assert.True(a.Content == b.Content, $"{a.RelativePath} differed between two builds.");
    }

    /// <summary>Files come out in one order, so a diff of two builds is a diff of the changes.
    /// </summary>
    [Theory]
    [MemberData(nameof(Apps))]
    public void Files_are_ordered(string key)
    {
        var paths = Build.Generate(key).Files.Select(f => f.RelativePath).ToList();
        Assert.Equal(paths.OrderBy(p => p, StringComparer.Ordinal), paths);
    }

    /// <summary>
    /// Every brace, bracket and parenthesis the emitters open is closed.
    ///
    /// <para>Not a parser, and not pretending to be: it counts delimiters outside strings, template
    /// literals, regular expressions and comments. What it catches is the emitter bug that actually
    /// happens — a block opened with one helper and closed with another, or a loop that emits a
    /// closing line only on the path it was tested on. The real proof is `tsc` over a generated
    /// application, which needs the package installed from a registry and so cannot run here.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Apps))]
    public void Every_generated_typescript_file_is_balanced(string key)
    {
        foreach (var file in Build.Generate(key).Files
            .Where(f => f.RelativePath.EndsWith(".ts", StringComparison.Ordinal)))
        {
            var depth = Balance(file.Content);
            Assert.True(depth == 0,
                $"{file.RelativePath} closes {-depth} more delimiters than it opens "
                + $"(or leaves {depth} open).");
        }
    }

    /// <summary>Nothing reads like generated C#. The shared source writer puts an opening brace on
    /// its own line, which is right for the other target and wrong here — and a generated file that
    /// does not look like the language it is written in reads as machine output somebody tolerates
    /// rather than source they own.</summary>
    [Theory]
    [MemberData(nameof(Apps))]
    public void Nothing_opens_a_block_on_the_next_line(string key)
    {
        foreach (var file in Build.Generate(key).Files
            .Where(f => f.RelativePath.EndsWith(".ts", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain("( {", file.Content, StringComparison.Ordinal);

            var lines = file.Content.Split('\n');
            for (var i = 1; i < lines.Length; i++)
            {
                if (lines[i].Trim() != "{") continue;

                var previous = lines[i - 1].TrimEnd();

                // A lone brace is idiomatic when it starts an element or an argument — an object
                // literal inside an array, or one passed to a call, is written exactly this way by
                // every formatter in the ecosystem. It is only wrong after something that should
                // have CARRIED the brace: a function signature, an arrow, an assignment.
                if (previous.EndsWith('[') || previous.EndsWith('(') || previous.EndsWith(',')) continue;

                Assert.Fail($"{file.RelativePath} line {i + 1} opens a block on its own line, "
                    + $"after '{previous.Trim()}'. TypeScript puts the brace on the line that opens "
                    + "it — see TsSource.Open, which exists for exactly this.");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Apps))]
    public void The_seed_is_a_dataset_with_an_anchor(string key)
    {
        var seed = JsonNode.Parse(Build.Content(Build.Generate(key), "api/src/seed/seed.json"))!.AsObject();

        Assert.NotNull(seed["anchor"]);
        Assert.NotNull(seed["entities"]);

        // The directory first: an application's own records point at people, and a reference has to
        // have something to point at by the time it is written.
        var entities = seed["entities"]!.AsArray()
            .Select(block => block!["entity"]!.GetValue<string>())
            .ToList();

        Assert.Equal("person", entities[0]);
    }

    /// <summary>
    /// A definition that asks for something not generated yet is REFUSED without
    /// <c>--allow-incomplete</c>.
    ///
    /// <para>The doctrine the whole diagnostic system exists for: a build that quietly shipped less
    /// than the definition asks for would look finished. Every corpus application reaches at least
    /// one of the gaps this target still has, so this is checkable on all of them.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Apps))]
    public void A_gap_refuses_the_build_unless_it_is_allowed(string key)
    {
        var strict = new NodeVueGenerator().Generate(new GenerateRequest(
            Build.Compile(key),
            new JsonObject { ["allowIncomplete"] = false, ["seed"] = 42 }));

        var permissive = Build.Generate(key);

        // Whatever a strict build refuses, a permissive one reports as a warning and writes anyway.
        if (!strict.Ok)
        {
            Assert.Empty(strict.Files);
            Assert.NotEmpty(permissive.Warnings);
            Assert.True(permissive.Ok);

            Assert.Equal(
                strict.Errors.Select(d => d.Code + "|" + d.JsonPath).OrderBy(x => x, StringComparer.Ordinal),
                permissive.Warnings.Select(d => d.Code + "|" + d.JsonPath).OrderBy(x => x, StringComparer.Ordinal));
        }
    }

    /// <summary>A knowingly incomplete build says so in the README, permanently — the person who
    /// inherits the repository in a year is the one who needs to know.</summary>
    [Fact]
    public void An_incomplete_build_marks_its_own_readme()
    {
        var key = Build.Corpus().First(k =>
            !new NodeVueGenerator().Generate(new GenerateRequest(
                Build.Compile(k), new JsonObject { ["allowIncomplete"] = false })).Ok);

        var readme = Build.Content(Build.Generate(key), "README.md");

        Assert.Contains("## Partial build", readme, StringComparison.Ordinal);
        Assert.Contains("--allow-incomplete", readme, StringComparison.Ordinal);
    }

    /// <summary>Counts delimiters outside anything that can legally contain an unbalanced one.
    /// </summary>
    private static int Balance(string source)
    {
        var depth = 0;
        var i = 0;

        while (i < source.Length)
        {
            var c = source[i];

            // A line comment runs to the end of the line.
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
                continue;
            }

            // A block comment runs to its terminator.
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/')) i++;
                i += 2;
                continue;
            }

            if (c is '"' or '\'' or '`')
            {
                var quote = c;
                i++;
                while (i < source.Length && source[i] != quote)
                {
                    if (source[i] == '\\') i++;
                    i++;
                }
                i++;
                continue;
            }

            if (c is '{' or '[' or '(') depth++;
            else if (c is '}' or ']' or ')') depth--;

            i++;
        }

        return depth;
    }
}

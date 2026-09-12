// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.SourceGen;
using Cordango.SourceGen.DotNet;

namespace Cordango.Node.Tests;

/// <summary>
/// **The front end is one tree, not two.**
///
/// <para>This is the load-bearing claim of having a second target at all. The Vue shell is a REST
/// client: it talks to whatever answers the HTTP contract and never asks what the backend is
/// written in. So a definition renders the same screens on both stacks — not because two emitters
/// were kept in step, but because there is one emitter.</para>
///
/// <para>Asserted rather than asserted-in-a-comment, because the way this would break is quiet: a
/// target adds a file under <c>web/</c> "just for now", or substitutes a token the other one does
/// not, and six months later the two front ends differ in ways nobody diffed. These tests fail on
/// the first divergence, naming the file.</para>
/// </summary>
public class SharedWebTests
{
    /// <summary>The two files that legitimately differ, and the whole of what a backend gets to say
    /// about the front end: where the built bundle lands, and which port `npm run dev` proxies to.
    /// Everything else being identical is the point.</summary>
    private const string ViteConfig = "web/vite.config.js";

    [Theory]
    [MemberData(nameof(Apps))]
    public void Both_targets_emit_the_same_web_tree(string key)
    {
        var (node, dotnet) = Both(key);

        var nodeWeb = WebFiles(node);
        var dotnetWeb = WebFiles(dotnet);

        Assert.Equal(dotnetWeb.Keys.OrderBy(k => k, StringComparer.Ordinal),
            nodeWeb.Keys.OrderBy(k => k, StringComparer.Ordinal));

        foreach (var (path, content) in nodeWeb)
        {
            if (path == ViteConfig) continue;

            Assert.True(dotnetWeb[path] == content,
                $"{path} differs between node-vue and dotnet-vue. The Vue shell is shared, so a "
                + "difference here means one target has started substituting or emitting something "
                + "the other does not.");
        }
    }

    /// <summary>
    /// The one file that differs, differing only where it should.
    ///
    /// <para>ASP.NET Core serves static files out of <c>wwwroot</c>; the Node host serves a
    /// directory it is handed. Both are substituted rather than hard-coded, which is what stopped
    /// the shared config from quietly describing whichever target was written first.</para>
    /// </summary>
    [Fact]
    public void The_vite_config_differs_only_in_where_the_bundle_lands()
    {
        var (node, dotnet) = Both("expenses");

        var nodeConfig = Build.Content(node, ViteConfig);
        var dotnetConfig = Build.Content(dotnet, ViteConfig);

        Assert.Contains("outDir: '../api/public'", nodeConfig, StringComparison.Ordinal);
        Assert.Contains("outDir: '../api/wwwroot'", dotnetConfig, StringComparison.Ordinal);

        // And nothing else. Normalising the two differing lines must leave two identical files.
        Assert.Equal(
            dotnetConfig.Replace("../api/wwwroot", "<out>", StringComparison.Ordinal),
            nodeConfig.Replace("../api/public", "<out>", StringComparison.Ordinal));
    }

    /// <summary>
    /// No SCAFFOLD placeholder survives into a generated front end.
    ///
    /// <para>Checked against the scaffold's own token list rather than against <c>{{</c>, which is
    /// also Vue's interpolation syntax — every template in the tree is full of it legitimately. The
    /// token list is the one the substitution pass reads, so a token added there and not substituted
    /// fails here rather than shipping as literal text on a screen.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Apps))]
    public void No_scaffold_token_survives_into_the_web_tree(string key)
    {
        foreach (var (path, content) in WebFiles(Build.Generate(key)))
            foreach (var token in SourceGen.Node.Scaffold.Tokens)
                Assert.False(content.Contains(token, StringComparison.Ordinal),
                    $"{path} still contains the placeholder {token}.");
    }

    /// <summary>The same, for the backend this target actually owns.</summary>
    [Theory]
    [MemberData(nameof(Apps))]
    public void No_scaffold_token_survives_into_the_api(string key)
    {
        var api = Build.Generate(key).Files
            .Where(f => f.RelativePath.StartsWith("api/", StringComparison.Ordinal)
                || f.RelativePath is "Dockerfile" or "docker-compose.yml" or "README.md"
                    or ".env.example" or ".gitignore" or ".dockerignore");

        foreach (var file in api)
            foreach (var token in SourceGen.Node.Scaffold.Tokens)
                Assert.False(file.Content.Contains(token, StringComparison.Ordinal),
                    $"{file.RelativePath} still contains the placeholder {token}.");
    }

    /// <summary>The capability sets are the same, and that is deliberate: a capability is a claim
    /// about what a STANDALONE application can be, and both targets produce one. What differs
    /// between them is what the emitters have got to, which is reported as CORD23xx and is not a
    /// capability.</summary>
    [Fact]
    public void Both_targets_claim_the_same_capabilities()
    {
        var node = new SourceGen.Node.NodeVueGenerator().Capabilities;
        var dotnet = new DotNetVueGenerator().Capabilities;

        Assert.Equal(Sorted(dotnet.Blocks.Supported), Sorted(node.Blocks.Supported));
        Assert.Equal(Sorted(dotnet.Effects.Supported), Sorted(node.Effects.Supported));
        Assert.Equal(Sorted(dotnet.Triggers.Supported), Sorted(node.Triggers.Supported));
        Assert.Equal(Sorted(dotnet.FieldTypes.Supported), Sorted(node.FieldTypes.Supported));
        Assert.Equal(Sorted(dotnet.PlatformTargets.Supported), Sorted(node.PlatformTargets.Supported));
        Assert.Equal(Sorted(dotnet.PlatformEntities.Supported), Sorted(node.PlatformEntities.Supported));

        Assert.Equal(Sorted(dotnet.Blocks.Withheld.Keys), Sorted(node.Blocks.Withheld.Keys));
        Assert.Equal(Sorted(dotnet.Effects.Withheld.Keys), Sorted(node.Effects.Withheld.Keys));
        Assert.Equal(Sorted(dotnet.Triggers.Withheld.Keys), Sorted(node.Triggers.Withheld.Keys));
    }

    public static TheoryData<string> Apps() => Build.CorpusData();

    private static string[] Sorted(IEnumerable<string> values) =>
        [.. values.OrderBy(v => v, StringComparer.Ordinal)];

    private static Dictionary<string, string> WebFiles(GenerateResult result) =>
        result.Files
            .Where(f => f.RelativePath.StartsWith("web/", StringComparison.Ordinal))
            .ToDictionary(f => f.RelativePath, f => f.Content, StringComparer.Ordinal);

    private static (GenerateResult Node, GenerateResult DotNet) Both(string key)
    {
        var app = Build.Compile(key);
        var options = new JsonObject { ["allowIncomplete"] = true, ["seed"] = 42 };

        return (
            new SourceGen.Node.NodeVueGenerator().Generate(new GenerateRequest(app, options)),
            new DotNetVueGenerator().Generate(new GenerateRequest(
                app, new JsonObject { ["allowIncomplete"] = true, ["seed"] = 42 })));
    }
}

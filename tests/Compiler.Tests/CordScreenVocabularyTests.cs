// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Cord;
using Cordango.Definition;
using Cordango.TestCorpus;
using Xunit.Abstractions;

namespace Cordango.Compiler.Tests;

public class CordScreenVocabularyTests(ITestOutputHelper output)
{
    private static readonly string[] Lowered = ["view", "stat", "chart", "text"];

    private static void Walk(JsonObject block, Dictionary<string, int> containers, Dictionary<string, int> content)
    {
        var nested = false;

        void Into(JsonNode? node)
        {
            foreach (var child in (node as JsonArray ?? []).OfType<JsonObject>())
            {
                Walk(child, containers, content);
                nested = true;
            }
        }

        Into(block["blocks"]);
        foreach (var column in (block["columns"] as JsonArray ?? []).OfType<JsonArray>()) Into(column);
        foreach (var tab in (block["tabs"] as JsonArray ?? []).OfType<JsonObject>()) Into(tab["blocks"]);

        if ((string?)block["kind"] is not { } kind) return;
        var into = nested ? containers : content;
        into[kind] = into.GetValueOrDefault(kind) + 1;
    }

    [Fact]
    public void What_the_screen_vocabulary_already_covers()
    {
        Dictionary<string, int> containers = new(StringComparer.Ordinal), content = new(StringComparer.Ordinal);
        foreach (string path in Corpus.SemanticCorpus())
        {
            var doc = JsonNode.Parse(File.ReadAllText(path))!;
            doc = Normalizer.Repair(doc, Schemas.AppDefinitionSchemaNode()) ?? doc;
            foreach (var page in (doc["pages"] as JsonArray ?? []).OfType<JsonObject>())
                foreach (var block in (page["blocks"] as JsonArray ?? []).OfType<JsonObject>())
                    Walk(block, containers, content);
        }

        var total = content.Values.Sum();
        var covered = content.Where(c => Lowered.Contains(c.Key)).Sum(c => c.Value);

        output.WriteLine($"CONTENT — what the author is saying: {covered}/{total} already sayable");
        foreach (var (kind, count) in content.OrderByDescending(c => c.Value))
            output.WriteLine($"{count,5}  {kind}{(Lowered.Contains(kind) ? "" : "   <- cannot say")}");

        output.WriteLine("");
        output.WriteLine($"LAYOUT — how it is arranged: {containers.Values.Sum()} containers, "
            + "none of which an author writes");
        foreach (var (kind, count) in containers.OrderByDescending(c => c.Value))
            output.WriteLine($"{count,5}  {kind}");

        Assert.Equal(ContentCovered, covered);
        Assert.Equal(ContentTotal, total);
        Assert.Equal(Containers, containers.Values.Sum());
    }

    private const int ContentCovered = 215;
    private const int ContentTotal = 294;
    private const int Containers = 175;
}

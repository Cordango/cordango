// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.Compile;

/// <summary>One contract that matched, and why.</summary>
/// <param name="Matched">The parts that hit, as <c>section:value</c> — shown to whoever asked so a
/// result never has to be taken on trust.</param>
public sealed record ContractMatch(JsonObject Contract, double Score, IReadOnlyList<string> Matched);

/// <summary>
/// Find the apps worth looking at, out of everything a tenant holds.
///
/// <para><b>Search, not lookup, and that is the whole point.</b> Handing an agent a list of the
/// first forty apps and a tool that fetches one BY KEY only works while it can already see the key
/// it needs. In a tenant with a hundred apps the relevant one is off the end of the list, so the
/// agent models a supplier from scratch beside the supplier app that already exists — the same
/// failure the core-app list was written to stop, one order of magnitude further out.</para>
///
/// <para><b>Lexical, and honest about it.</b> Tokens against keys, labels, purpose, event names,
/// action ids and rule ids, weighted so an exact key beats a label and a label beats a word in
/// prose. No embeddings and no model call: this runs inside <c>cordango discover</c> on a laptop
/// with no network, and a ranker that needs a server is a ranker the CLI cannot have. What matters
/// is the interface — a better ranker drops in behind it without moving anything.</para>
/// </summary>
public static class ContractSearch
{
    public static IReadOnlyList<ContractMatch> Rank(
        IReadOnlyList<JsonObject> contracts, string? query, int limit = 10, string? section = null)
    {
        ArgumentNullException.ThrowIfNull(contracts);
        var terms = Tokens(query);
        if (terms.Count == 0)
            return [.. contracts.Take(Math.Max(0, limit)).Select(c => new ContractMatch(c, 0, []))];

        var scored = new List<ContractMatch>();
        foreach (var contract in contracts)
        {
            var (score, matched) = Score(contract, terms, section);
            if (score > 0) scored.Add(new ContractMatch(contract, score, matched));
        }

        return [.. scored
            .OrderByDescending(m => m.Score)
            // Ties break on the app key, so two equally good answers come back in the same order
            // every time rather than in whatever order the tenant's apps happened to load.
            .ThenBy(m => Key(m.Contract), StringComparer.Ordinal)
            .Take(Math.Max(0, limit))];
    }

    private static (double Score, List<string> Matched) Score(
        JsonObject contract, IReadOnlyList<string> terms, string? section)
    {
        double score = 0;
        var matched = new List<string>();
        var wantAll = section is null or "all";

        void Hit(string where, string? candidate, double exact, double partial)
        {
            if (candidate is null) return;
            var lowered = candidate.ToLowerInvariant();
            foreach (var term in terms)
            {
                if (lowered == term) { score += exact; matched.Add($"{where}:{candidate}"); }
                else if (lowered.Contains(term, StringComparison.Ordinal))
                {
                    score += partial;
                    matched.Add($"{where}:{candidate}");
                }
            }
        }

        // What the app IS counts only when the question was not narrowed to one section. Asking for
        // events and getting an app whose PURPOSE mentions the word, with no such event anywhere, is
        // the answer to a question nobody asked.
        if (wantAll)
        {
            var identity = contract["identity"] as JsonObject;
            Hit("app", Str(identity, "key"), 6, 3);
            Hit("app", Str(identity, "name"), 5, 2.5);

            // Purpose is prose, so every word of it hits weakly — and PARTIALLY, which is the half
            // that matters: the sentence says "suppliers" and the question asks about a "supplier",
            // and an exact-only comparison finds nothing at all.
            foreach (var word in Tokens(Str(contract["purpose"] as JsonObject, "summary")))
                Hit("purpose", word, 1, 0.5);
            foreach (var duty in (contract["purpose"]?["duties"] as JsonArray ?? []))
                foreach (var word in Tokens(duty?.GetValue<string>()))
                    Hit("purpose", word, 0.8, 0.4);
        }

        if (wantAll || section == "entities")
            foreach (var e in contract["entities"] as JsonArray ?? [])
            {
                Hit("entity", Str(e as JsonObject, "key"), 5, 2);
                Hit("entity", Str(e as JsonObject, "label"), 4, 1.5);
            }

        if (wantAll || section == "events")
            foreach (var e in contract["events"] as JsonArray ?? [])
                Hit("event", Str(e as JsonObject, "name"), 5, 2);

        if (wantAll || section == "actions")
            foreach (var a in contract["actions"] as JsonArray ?? [])
            {
                Hit("action", Str(a as JsonObject, "id"), 5, 2);
                Hit("action", Str(a as JsonObject, "label"), 4, 1.5);
            }

        if (wantAll || section == "rules")
            foreach (var r in contract["rules"] as JsonArray ?? [])
                Hit("rule", Str(r as JsonObject, "id"), 4, 1.5);

        return (score, [.. matched.Distinct(StringComparer.Ordinal)]);
    }

    /// <summary>Lowercased words of two characters or more. Punctuation and the dots inside
    /// <c>deal.won</c> are separators, so searching for "won" finds the event.</summary>
    private static IReadOnlyList<string> Tokens(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var parts = text.ToLowerInvariant()
            .Split(['.', ' ', '_', '-', ',', ';', ':', '/', '(', ')', '\'', '"', '\t', '\n', '\r'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return [.. parts.Where(p => p.Length >= 2).Distinct(StringComparer.Ordinal)];
    }

    private static string? Key(JsonObject contract) => Str(contract["identity"] as JsonObject, "key");

    private static string? Str(JsonObject? o, string key) =>
        o?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

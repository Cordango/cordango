// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace Cordango.Definition.Blueprints;

/// <summary>How ready a blueprint is to be approved, and what is standing in the way.</summary>
/// <param name="Ready">No blocking uncertainty is unresolved and the gate is clean.</param>
/// <param name="Resolved">Uncertainties that have an answer.</param>
/// <param name="Open">Uncertainties still unanswered.</param>
/// <param name="Blocking">Of those, the ones that would produce the wrong app if guessed.</param>
public sealed record Completeness(bool Ready, int Resolved, int Open, int Blocking)
{
    /// <summary>A rough share for a progress indicator. Deliberately not presented as certainty —
    /// it counts questions, and questions are not equally sized.</summary>
    public double Share => Resolved + Open == 0 ? 1 : (double)Resolved / (Resolved + Open);
}

/// <summary>
/// The next-question engine: which single unresolved decision is worth the user's attention now.
///
/// <para><b>Why ranking rather than a fixed script.</b> A questionnaire asks everything, so it is
/// long, and length is what makes people click through it without reading. Ranking means a simple
/// tracker gets a handful of questions and a multi-role workspace gets the full conversation,
/// without either being designed separately.</para>
///
/// <para>The score is <c>value × impact ÷ effort</c>: how much uncertainty an answer removes, how
/// much of the app's architecture it changes, divided by how much work it is to answer. Everything
/// here is deterministic — the model phrases the question and reads the answer, but which question
/// comes next is arithmetic, so it can be tested and explained.</para>
/// </summary>
public static class NextQuestion
{
    /// <summary>Layers ordered by how much of the app depends on them. A wrong intent invalidates
    /// everything after it; a wrong scenario invalidates a test. Answering in dependency order is
    /// also what stops the interview asking about screens before it knows what the app is for.</summary>
    private static readonly Dictionary<string, int> LayerImpact = new(StringComparer.Ordinal)
    {
        [SpecLayers.Intent] = 10,
        [SpecLayers.Concepts] = 8,
        [SpecLayers.Relationships] = 7,
        [SpecLayers.Workflows] = 6,
        [SpecLayers.Actors] = 5,
        [SpecLayers.Data] = 4,
        [SpecLayers.Jobs] = 3,
        [SpecLayers.Experience] = 3,
        [SpecLayers.Scenarios] = 2,
    };

    /// <summary>Effort, as the user experiences it. A question with suggested answers is a tap; an
    /// open one is typing, and typing is what makes people abandon a wizard.</summary>
    private static int Effort(Uncertainty u) => u.SuggestedAnswers.Count switch
    {
        0 => 3,        // free text
        <= 4 => 1,     // a short pick list
        _ => 2,        // a long one still means reading
    };

    /// <summary>What answering removes. A blocking question is worth more because the alternative to
    /// asking it is guessing, and a guess here is not a default — it is a different app.</summary>
    private static int Value(Uncertainty u) => u.BlocksLowering ? 5 : 2;

    public static double Score(Uncertainty u) =>
        Value(u) * LayerImpact.GetValueOrDefault(u.AffectsLayer, 1) / (double)Effort(u);

    /// <summary>Unanswered questions, best first. Ties break on id so the order is stable — an
    /// interview that reshuffled its own queue between calls would be impossible to resume.</summary>
    public static IReadOnlyList<Uncertainty> Rank(Blueprint bp) =>
        bp.Uncertainties
            .Where(u => u.Resolution is null)
            .OrderByDescending(Score)
            .ThenBy(u => u.Id, StringComparer.Ordinal)
            .ToList();

    /// <summary>The one question to ask now, or null when nothing is left worth asking.</summary>
    public static Uncertainty? Next(Blueprint bp) => Rank(bp).FirstOrDefault();

    /// <summary>
    /// Where the blueprint stands.
    ///
    /// <para>Only a BLOCKING uncertainty stops approval. Everything else becomes a recorded
    /// assumption the user can see and override — blocking on every open question would turn the
    /// conversation back into the form it replaced.</para>
    /// </summary>
    public static Completeness Measure(Blueprint bp)
    {
        var resolved = bp.Uncertainties.Count(u => u.Resolution is not null);
        var open = bp.Uncertainties.Count(u => u.Resolution is null);
        var blocking = bp.Uncertainties.Count(u => u.Resolution is null && u.BlocksLowering);
        var ready = blocking == 0 && BlueprintGate.Validate(bp).Count == 0;
        return new Completeness(ready, resolved, open, blocking);
    }

    /// <summary>
    /// Turns every unresolved non-blocking question into a recorded assumption at approval time.
    ///
    /// <para>The alternative — dropping them — is what makes generated apps feel arbitrary: a
    /// decision was made, the user never saw it, and the app does something they would have chosen
    /// differently. Writing them down costs a line each and makes the guesses reviewable.</para>
    /// </summary>
    public static Blueprint RecordOpenQuestionsAsAssumptions(Blueprint bp)
    {
        var open = bp.Uncertainties.Where(u => u.Resolution is null && !u.BlocksLowering).ToList();
        if (open.Count == 0) return bp;

        var assumptions = bp.Assumptions.ToList();
        var uncertainties = bp.Uncertainties.ToList();

        foreach (var u in open)
        {
            var chosen = u.SuggestedAnswers.FirstOrDefault();
            assumptions.Add(new Assumption
            {
                Id = $"asm_{u.Id}",
                Statement = chosen is null
                    ? $"Nobody answered: {u.Question}"
                    : $"{u.Question} — assumed: {chosen}",
                Layer = u.AffectsLayer,
                Revision = bp.Revision,
            });

            var index = uncertainties.FindIndex(x => x.Id == u.Id);
            uncertainties[index] = u with
            {
                Resolution = new UncertaintyResolution
                {
                    Source = ResolutionSources.AssumedDefault,
                    Value = chosen,
                    Revision = bp.Revision,
                },
            };
        }

        return bp with { Assumptions = assumptions, Uncertainties = uncertainties };
    }
}

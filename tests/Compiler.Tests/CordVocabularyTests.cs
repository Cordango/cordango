// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.Cord;

namespace Cordango.Compiler.Tests;

public class CordVocabularyTests
{
    /// <summary>
    /// <b>Raising is unambiguous, which is what lets a value that is already a word raise to itself.</b>
    ///
    /// <para><see cref="CordWordMap.Raise"/> tries the lowered spelling first and then passes a known
    /// word through, because the model holds BOTH: an authored app carries <c>schedule.daily</c> and an
    /// imported one carries <c>schedule</c>. That shortcut is only sound while no word is some other
    /// pair's lowered value — if one ever were, raising it would silently return the wrong word and
    /// <see cref="CordDocument"/> would write down a different application than it was given.</para>
    ///
    /// <para>True of all seven maps today. This is the assertion that keeps it true when the eighth
    /// arrives, and it costs nothing to hold.</para>
    /// </summary>
    [Fact]
    public void No_word_is_another_pairs_lowered_value()
    {
        foreach (var map in CordVocabulary.All)
            foreach (var (word, _) in map.Pairs)
            {
                var clash = map.Pairs.FirstOrDefault(p => p.Lowered == word && p.Word != word);
                Assert.True(clash.Word is null,
                    $"{map.Name}: '{word}' is a word AND what '{clash.Word}' lowers to, so raising it "
                    + "is ambiguous — one of the two names has to change.");
            }
    }

    /// <summary>Every word survives the trip out and back, which is the property the writer rests on.</summary>
    [Fact]
    public void Every_word_survives_lowering_and_raising()
    {
        foreach (var map in CordVocabulary.All)
            foreach (var (word, lowered) in map.Pairs)
            {
                Assert.Equal(word, map.Raise(lowered));
                Assert.Equal(word, map.Raise(word));
                Assert.Equal(lowered, map.Lower(word));
            }
    }

    /// <summary>A word from nowhere raises to NOTHING — never to itself, and never to something close.
    /// The writer reads null as "no operation can say this" and reports it; a fallback here would turn
    /// that report into a document the ops schema refuses.</summary>
    [Fact]
    public void An_unknown_value_does_not_raise()
    {
        Assert.Null(CordVocabulary.Placements.Raise("listHeader"));
        Assert.Null(CordVocabulary.EffectTypes.Raise("createForEach"));
        Assert.Null(CordVocabulary.ConditionOperators.Raise("between"));
        Assert.Null(CordVocabulary.TriggerEvents.Raise(CordVocabulary.FieldChanged));
    }
}

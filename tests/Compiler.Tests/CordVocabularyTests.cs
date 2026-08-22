// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.Cord;

namespace Cordango.Compiler.Tests;

public class CordVocabularyTests
{
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

    [Fact]
    public void An_unknown_value_does_not_raise()
    {
        Assert.Null(CordVocabulary.Placements.Raise("listHeader"));
        Assert.Null(CordVocabulary.EffectTypes.Raise("createForEach"));
        Assert.Null(CordVocabulary.ConditionOperators.Raise("between"));
        Assert.Null(CordVocabulary.TriggerEvents.Raise(CordVocabulary.FieldChanged));
    }
}

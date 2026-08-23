// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.SourceGen.DotNetVue;

namespace Cordango.Compatibility.Tests;

public class CapabilityCoverageTests
{
    private static readonly GeneratorCapabilities Standalone = new DotNetVueGenerator().Capabilities;

    public static TheoryData<string> Axes() =>
        new("blocks", "effects", "triggers", "fieldTypes");

    [Theory]
    [MemberData(nameof(Axes))]
    public void Every_value_the_schema_allows_is_supported_or_withheld_with_a_reason(string axis)
    {
        var (set, language) = axis switch
        {
            "blocks" => (Standalone.Blocks, DefinitionVocabulary.BlockKinds),
            "effects" => (Standalone.Effects, DefinitionVocabulary.EffectTypes),
            "triggers" => (Standalone.Triggers, DefinitionVocabulary.TriggerEvents),
            "fieldTypes" => (Standalone.FieldTypes, DefinitionVocabulary.FieldTypes),
            _ => throw new ArgumentOutOfRangeException(nameof(axis)),
        };

        var unclassified = language.Except(set.Known).OrderBy(v => v, StringComparer.Ordinal).ToList();
        Assert.True(unclassified.Count == 0,
            $"the schema allows {axis} the standalone target has not classified: "
            + string.Join(", ", unclassified)
            + ". Add each one to Supported, or to Withheld with the reason it is not built.");

        var unknown = set.Known.Except(language).OrderBy(v => v, StringComparer.Ordinal).ToList();
        Assert.True(unknown.Count == 0,
            $"the standalone target names {axis} the schema does not define: " + string.Join(", ", unknown));
    }

    [Fact]
    public void Every_withheld_value_explains_itself_in_a_sentence_rather_than_a_word()
    {
        foreach (var (axis, set) in new (string, CapabilitySet)[]
                 {
                     ("blocks", Standalone.Blocks), ("effects", Standalone.Effects),
                     ("triggers", Standalone.Triggers), ("fieldTypes", Standalone.FieldTypes),
                     ("platformApps", Standalone.PlatformTargets),
                     ("platformEntities", Standalone.PlatformEntities),
                 })
        {
            foreach (var (value, why) in set.Withheld)
                Assert.True(why.Length > 40,
                    $"{axis}.{value} is withheld with only '{why}' — say why, not that.");
        }
    }

    [Fact]
    public void The_open_axes_carry_a_standing_explanation()
    {
        Assert.False(string.IsNullOrWhiteSpace(Standalone.PlatformTargets.Fallback));
        Assert.False(string.IsNullOrWhiteSpace(Standalone.PlatformEntities.Fallback));

        Assert.Contains("Cordango Platform", Standalone.PlatformTargets.Explain("some_other_app"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Every_diagnostic_code_is_distinct()
    {
        Assert.Equal(DiagnosticCodes.All.Count, DiagnosticCodes.All.Distinct(StringComparer.Ordinal).Count());
        Assert.All(DiagnosticCodes.All, c => Assert.StartsWith("CORD21", c, StringComparison.Ordinal));
    }

    [Fact]
    public void Every_not_yet_code_is_distinct_and_none_is_a_retired_one()
    {
        Assert.Equal(NotYetCodes.All.Count, NotYetCodes.All.Distinct(StringComparer.Ordinal).Count());
        Assert.All(NotYetCodes.All, c => Assert.StartsWith("CORD23", c, StringComparison.Ordinal));
        Assert.Empty(NotYetCodes.All.Intersect(NotYetCodes.Retired, StringComparer.Ordinal));
    }
}

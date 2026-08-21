// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.SourceGen.DotNetVue;

namespace Cordango.Compatibility.Tests;

/// <summary>
/// Every value the language allows is either built or refused ON PURPOSE.
///
/// <para>This is the rule that keeps a capability model honest as the language grows. Add a block
/// kind to the schema and a target that has not been taught about it will, without this, silently
/// treat it as unsupported with the message "not something this target knows how to build" — which
/// is true, uninformative, and indistinguishable from a considered decision. Failing here forces
/// the choice to be made and written down at the moment the vocabulary changes.</para>
///
/// <para>Same argument, same shape, as
/// <c>Every_value_the_platform_accepts_is_offered_or_withheld_on_purpose</c> in the Cord vocabulary
/// suite. That one exists because an agent could not tell a gap from a decision; so does this.</para>
/// </summary>
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

        // And nothing invented: a capability naming a value the language does not have is either a
        // typo or a memory of a feature that was renamed, and both read as support that is not there.
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

    /// <summary>The open axis. <c>targetApp</c> can name any other application's key, so it cannot
    /// be enumerated — which makes the standing explanation the only thing a user will ever see for
    /// a cross-app reference, and it had better be there.</summary>
    [Fact]
    public void The_open_axes_carry_a_standing_explanation()
    {
        Assert.False(string.IsNullOrWhiteSpace(Standalone.PlatformTargets.Fallback));
        Assert.False(string.IsNullOrWhiteSpace(Standalone.PlatformEntities.Fallback));

        // The point of a fallback is the value nobody listed, and it has to meet the same bar as a
        // listed one: name the platform rather than answering "unsupported".
        Assert.Contains("Cordango Platform", Standalone.PlatformTargets.Explain("some_other_app"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Every_diagnostic_code_is_distinct()
    {
        Assert.Equal(DiagnosticCodes.All.Count, DiagnosticCodes.All.Distinct(StringComparer.Ordinal).Count());
        Assert.All(DiagnosticCodes.All, c => Assert.StartsWith("CORD21", c, StringComparison.Ordinal));
    }
}

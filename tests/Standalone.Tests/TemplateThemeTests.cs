// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.RegularExpressions;
using Cordango.SourceGen.DotNetVue;

namespace Cordango.Standalone.Tests;

/// <summary>
/// The palette a generated application ships with, and the one way it can be wrong in silence.
///
/// <para><b>An undefined theme token is not a fallback.</b> Vuetify writes <c>--v-theme-X</c> only
/// for a key that is PRESENT in a palette's <c>colors</c>, and a CSS declaration naming a variable
/// that was never defined is invalid — so the browser discards the whole declaration and reports
/// nothing. What arrives on screen is a control with no background or no border, which reads as a
/// broken component rather than as a missing palette entry.</para>
///
/// <para>That makes light-and-dark PARITY the thing worth pinning. A token added to one palette and
/// forgotten in the other produces an application that is correct in whichever mode its author
/// happened to be using and quietly broken in the other, and nobody reads two colour lists side by
/// side looking for the difference.</para>
/// </summary>
public class TemplateThemeTests
{
    private static readonly ScaffoldOptions App = new("Expenses", "expenses", "Expenses");

    /// <summary>
    /// The tokens the shared controls' own CSS reads, which Vuetify's default palette does NOT
    /// carry. Named here because a generated application has to define them before a control from
    /// <c>@cordango/web-controls</c> will render; the package pins the same pair from its side, in
    /// <c>HOST_THEME_TOKENS</c>, against what its components actually read.
    /// </summary>
    private static readonly string[] SharedControlTokens = ["surface-2", "outline"];

    [Fact]
    public void Both_palettes_define_the_same_tokens()
    {
        var (light, dark) = Palettes();

        Assert.Equal(
            light.OrderBy(k => k, StringComparer.Ordinal),
            dark.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void The_tokens_the_shared_controls_read_are_defined()
    {
        var (light, dark) = Palettes();

        foreach (var token in SharedControlTokens)
        {
            Assert.True(light.Contains(token), $"the light palette does not define '{token}'");
            Assert.True(dark.Contains(token), $"the dark palette does not define '{token}'");
        }
    }

    /// <summary>
    /// The colour keys of each palette, read out of the template as shipped.
    ///
    /// <para>Parsed rather than imported, because this is JavaScript and the assertion is about what
    /// a generated application receives — the file, not a model of it.</para>
    /// </summary>
    private static (HashSet<string> Light, HashSet<string> Dark) Palettes()
    {
        var theme = Scaffold.Files(App)
            .Single(f => f.RelativePath == "web/src/theme.js").Content;

        return (Colors(theme, "light"), Colors(theme, "dark"));
    }

    private static HashSet<string> Colors(string theme, string mode)
    {
        var start = theme.IndexOf($"export const {mode} = {{", StringComparison.Ordinal);
        Assert.True(start >= 0, $"the template has no '{mode}' palette");

        var open = theme.IndexOf("colors: {", start, StringComparison.Ordinal);
        Assert.True(open >= 0, $"the '{mode}' palette has no colors block");

        // To the matching brace, so a nested value cannot end the block early.
        var depth = 0;
        var at = theme.IndexOf('{', open);
        var end = at;
        for (; end < theme.Length; end++)
        {
            if (theme[end] == '{') depth++;
            else if (theme[end] == '}' && --depth == 0) break;
        }

        var block = theme[at..end];

        // `background: '#F4F6F9',` and `'surface-2': '#EDF0F5',` are the same declaration with and
        // without quotes — a hyphen is what forces the second spelling, and the hyphenated ones are
        // exactly the tokens this test cares most about.
        return Regex.Matches(block, @"^\s*'?([a-zA-Z][a-zA-Z0-9-]*)'?\s*:", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }
}

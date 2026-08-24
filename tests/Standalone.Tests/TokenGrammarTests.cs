// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.Definition;
using Cordango.Standalone.Conditions;

namespace Cordango.Standalone.Tests;

public class TokenGrammarTests
{
    private const string Actor = "person-1";
    private const string User = "user-1";

    private static readonly DateTimeOffset Now = new(2026, 3, 10, 9, 30, 0, TimeSpan.Zero);

    private static IEnumerable<string> Candidates()
    {
        foreach (var actor in ExprTokens.ActorTokens) yield return actor;

        foreach (var anchor in new[] { "today", "now" })
        {
            yield return anchor;

            foreach (var sign in new[] { "+", "-" })
            foreach (var amount in new[] { "1", "7", "30", "365" })
            foreach (var unit in new[] { "", "d", "w", "h", "m", "y" })
                yield return $"{anchor}{sign}{amount}{unit}";
        }

        yield return "tomorrow";
        yield return "yesterday";
        yield return "today++1";
        yield return "startOfMonth";
    }

    private static string Braced(string token) => "{{" + token + "}}";

    [Fact]
    public void The_gate_accepts_some_of_the_candidates_and_refuses_others()
    {
        var candidates = Candidates().ToArray();

        Assert.Contains(candidates, ExprTokens.IsKnown);
        Assert.Contains(candidates, t => !ExprTokens.IsKnown(t));
    }

    [Fact]
    public void The_runtime_resolves_every_token_the_gate_accepts()
    {
        var unresolved = Candidates()
            .Where(ExprTokens.IsKnown)
            .Where(token => ValueTokens.Fill(Braced(token), Actor, User, Now) == Braced(token))
            .ToArray();

        Assert.True(
            unresolved.Length == 0,
            "The gate accepts these and the runtime leaves them as literal text, which is written "
            + "into the field or compared against a date and never matches:\n  "
            + string.Join("\n  ", unresolved.Select(Braced)));
    }

    [Fact]
    public void The_runtime_and_the_gate_resolve_them_to_the_same_value()
    {
        var disagreements = Candidates()
            .Where(ExprTokens.IsKnown)
            .Select(token => (
                Token: token,
                Gate: ExprTokens.Resolve(token, Actor, Now),
                Runtime: ValueTokens.Resolve(Braced(token), Actor, User, Now)))
            .Where(row => row.Gate != row.Runtime)
            .ToArray();

        Assert.True(
            disagreements.Length == 0,
            "The gate and the runtime resolve these to different values:\n  "
            + string.Join("\n  ", disagreements.Select(r => $"{Braced(r.Token)} gate={r.Gate} runtime={r.Runtime}")));
    }

    [Fact]
    public void The_runtime_leaves_a_token_the_gate_refuses_as_written()
    {
        foreach (var token in Candidates().Where(t => !ExprTokens.IsKnown(t)))
            Assert.Equal(Braced(token), ValueTokens.Fill(Braced(token), Actor, User, Now));
    }

    [Theory]
    [InlineData("today+1w", "2026-03-17")]
    [InlineData("today-2w", "2026-02-24")]
    [InlineData("today+7d", "2026-03-17")]
    [InlineData("today-30d", "2026-02-08")]
    [InlineData("today+7", "2026-03-17")]
    [InlineData("today", "2026-03-10")]
    public void A_date_offset_resolves_to_the_day_it_names(string token, string expected)
    {
        Assert.Equal(expected, ValueTokens.Resolve(Braced(token), Actor, User, Now));
    }

    [Fact]
    public void An_hour_offset_on_a_date_anchor_is_refused_by_both()
    {
        Assert.False(ExprTokens.IsKnown("today-4h"));
        Assert.Equal("{{today-4h}}", ValueTokens.Fill("{{today-4h}}", Actor, User, Now));
    }

    [Fact]
    public void An_hour_offset_on_the_instant_anchor_resolves()
    {
        Assert.Equal("2026-03-10T05:30:00.0000000+00:00", ValueTokens.Resolve("{{now-4h}}", Actor, User, Now));
    }

    [Fact]
    public void A_month_offset_is_available_nowhere()
    {
        Assert.False(ExprTokens.IsKnown("today+1m"));
        Assert.Equal("{{today+1m}}", ValueTokens.Fill("{{today+1m}}", Actor, User, Now));
    }

    [Fact]
    public void An_offset_inside_a_longer_template_resolves()
    {
        Assert.Equal(
            "Due 2026-03-17, raised by person-1",
            ValueTokens.Fill("Due {{today+1w}}, raised by {{actor.id}}", Actor, User, Now));
    }
}

// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace Cordango.SourceGen;

/// <summary>
/// The CORD21xx range: "this is a valid Cordango application, and this target cannot build it."
///
/// <para><b>Nothing in this range is a defect in the definition.</b> The gate already said the
/// document is well formed and coherent; these codes are a target admitting a limit. That
/// distinction is the whole reason they are a separate range from validation errors — an author
/// seeing CORD2102 has not made a mistake, they have used a feature that needs the platform.</para>
///
/// <para><b>A capability is never silently dropped.</b> Generating an application with the audit
/// block quietly removed produces something that looks finished and is missing a feature nobody
/// will notice is gone until they look for last month's changes. Refusing is louder and kinder.</para>
/// </summary>
public static class DiagnosticCodes
{
    /// <summary>A reference into another installed application, which only exists where more than
    /// one application is installed.</summary>
    public const string CrossAppReference = "CORD2100";

    /// <summary>The <c>relatedApps</c> block: a scan across every other app in the workspace for
    /// records pointing at this one. Unimplementable where there is no workspace.</summary>
    public const string RelatedAppsBlock = "CORD2101";

    /// <summary>The <c>history</c> block, fed by platform auditing.</summary>
    public const string HistoryBlock = "CORD2102";

    /// <summary>The <c>enrich</c> effect: background research against the public web.</summary>
    public const string EnrichEffect = "CORD2103";

    /// <summary>An entity declaring a <c>series</c> partition — ordered rows where one row's value
    /// depends on the row before it.</summary>
    public const string SeriesEntity = "CORD2104";

    /// <summary><c>prev()</c> in a computed expression: reads the previous row of an ordered series.</summary>
    public const string PrevExpression = "CORD2105";

    /// <summary>A rollup with a <c>window</c> or a <c>match</c> — aggregation across a declared
    /// range or across siblings, rather than straight up a parent reference.</summary>
    public const string WindowedRollup = "CORD2106";

    /// <summary>An effect type this target does not implement.</summary>
    public const string UnsupportedEffect = "CORD2107";

    /// <summary>A workflow trigger this target does not implement.</summary>
    public const string UnsupportedTrigger = "CORD2108";

    /// <summary>A platform entity with no local equivalent to map onto.</summary>
    public const string UnsupportedPlatformTarget = "CORD2109";

    /// <summary>A feature that requires a model at runtime.</summary>
    public const string AiFeature = "CORD2110";

    /// <summary>A block kind this target cannot render.</summary>
    public const string UnsupportedBlock = "CORD2111";

    /// <summary>A field type this target cannot store.</summary>
    public const string UnsupportedFieldType = "CORD2112";

    /// <summary>Every code in the range, for the test that stops two of them meaning the same
    /// thing and for anything that wants to document the set.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        CrossAppReference, RelatedAppsBlock, HistoryBlock, EnrichEffect, SeriesEntity,
        PrevExpression, WindowedRollup, UnsupportedEffect, UnsupportedTrigger,
        UnsupportedPlatformTarget, AiFeature, UnsupportedBlock, UnsupportedFieldType,
    ];
}

/// <summary>
/// The CORD23xx range: "this target will build that one day, and today it does not."
///
/// <para>The difference from <see cref="DiagnosticCodes"/> is what a reader should do about it.
/// CORD21xx says wait for the platform or change the definition; CORD23xx says wait for a release,
/// which will remove it without anybody touching their definition. Reporting one as the other sends
/// somebody either to a workaround they did not need or to a wait that never ends.</para>
///
/// <para><b>Why they are listed rather than written where they are raised.</b> They were magic
/// strings in two files, and a retired code came back meaning something new: CORD2304 was once
/// "a command guard is not emitted", was fixed, and a test still asserts its absence. Reusing the
/// number made that test fail against a diagnostic about a table. A second target picking its own
/// numbers would have collided the same way, quietly.</para>
/// </summary>
public static class NotYetCodes
{
    /// <summary>A block kind the emitters have not got to.</summary>
    public const string Block = "CORD2301";

    /// <summary>A workflow trigger that is not wired, so nothing runs.</summary>
    public const string Trigger = "CORD2302";

    /// <summary>An effect declared on a workflow or a command that is not generated.</summary>
    public const string Effect = "CORD2303";

    // CORD2304 is RETIRED. It meant "a command guard is not emitted", which was fixed, and
    // CommandGuardTests still asserts that no application reports it. Do not reuse the number:
    // a new diagnostic wearing it makes that test fail for a reason that has nothing to do with
    // guards. Take the next free code instead.

    /// <summary>A computed field neither the expression emitter nor the rollup emitter can write.</summary>
    public const string Computed = "CORD2305";

    /// <summary>A guard condition the emitter cannot write, so the thing it guards would run
    /// without it.</summary>
    public const string Guard = "CORD2306";

    /// <summary>Rollups that count each other in a circle, so no evaluation order works them out.</summary>
    public const string RollupCycle = "CORD2307";

    /// <summary>One option ON a block that did render — a list's grouping, a calendar's day view.
    /// Separate from <see cref="Block"/> so a reader is told which part is missing rather than left
    /// to work out why the screen looks nearly right.</summary>
    public const string BlockOption = "CORD2308";

    /// <summary>An entity that opts into the cross-app calendar on a target that has no such thing.
    /// Not a block — the flag is on the ENTITY, and what it asks for is a surface spanning every app
    /// in a workspace, which a single generated application does not have to span.</summary>
    public const string Calendar = "CORD2309";

    /// <summary>Every code in the range, for the test that stops two of them meaning the same
    /// thing. The retired number is deliberately absent.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        Block, Trigger, Effect, Computed, Guard, RollupCycle, BlockOption, Calendar,
    ];

    /// <summary>Codes that once meant something, no longer do, and must not be handed to anything
    /// new — tests still assert their absence.</summary>
    public static readonly IReadOnlyList<string> Retired = ["CORD2304"];
}

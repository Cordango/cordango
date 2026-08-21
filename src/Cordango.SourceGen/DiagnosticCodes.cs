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

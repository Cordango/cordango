// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Cli.Generate;
using Cordango.Cli.Workspace;
using Cordango.SourceGen;

namespace Cordango.Cli.Commands;

/// <summary>
/// What this build can generate, and what each target deliberately will not.
///
/// <para><b>The withheld half is the point.</b> A target that only lists what it supports answers
/// "no" and stops, and somebody then reads the schema, finds the feature is perfectly legal, and
/// cannot tell whether it was forgotten, rejected on purpose, or planned for next month. Printing
/// the reason next to the refusal is the same argument <c>cordango vocabulary</c> already makes
/// about the authoring words, and it is why capabilities carry an explanation rather than a
/// boolean.</para>
///
/// <para>Offline and instant: capabilities are declared data, not something a generator has to run
/// to discover.</para>
/// </summary>
public static class TargetsCommand
{
    public static int Run(Args args, Output output)
    {
        var wanted = args.Value("target");
        var platform = wanted is { Length: > 0 }
            && string.Equals(wanted, BuildConfig.Platform, StringComparison.OrdinalIgnoreCase);

        IAppSourceGenerator[] targets;
        if (platform) targets = [];
        else if (wanted is { Length: > 0 }) targets = Targets.Find(wanted) is { } one ? [one] : [];
        else targets = [.. Targets.All];

        if (wanted is { Length: > 0 } && !platform && targets.Length == 0)
            return output.Fail($"no target called '{wanted}'",
                [$"known targets: {Targets.Known}, {BuildConfig.Platform}"],
                code: ExitCodes.Usage);

        var payload = new JsonObject
        {
            ["targets"] = new JsonArray([.. targets.Select(t => (JsonNode)Describe(t))]),

            // The platform is listed beside the generators because it is what `--target` and
            // `configure` will accept, and a list that omits an accepted value is a list somebody
            // has to correct from the error message instead.
            ["platform"] = new JsonObject
            {
                ["id"] = BuildConfig.Platform,
                ["generates"] = false,
                ["withholds"] = false,
                ["requiresConnection"] = true,
            },
        };

        return output.Ok(payload, w =>
        {
            foreach (var target in targets)
            {
                w.WriteLine($"{target.Id} {target.Version}");
                w.WriteLine();

                foreach (var (axis, set) in Axes(target.Capabilities))
                {
                    w.WriteLine($"  {axis}");
                    w.WriteLine($"    builds:    {Join(set.Supported)}");
                    if (set.Withheld.Count > 0)
                        w.WriteLine($"    will not:  {Join(set.Withheld.Keys)}");
                }

                w.WriteLine("  computed");
                w.WriteLine($"    builds:    expressions, rollups up a reference");
                var computed = new List<string>();
                if (!target.Capabilities.SeriesAndPrev) computed.Add("series, prev()");
                if (!target.Capabilities.WindowedRollups) computed.Add("windowed and matched rollups");
                if (computed.Count > 0) w.WriteLine($"    will not:  {string.Join(", ", computed)}");

                w.WriteLine();
                w.WriteLine("  `cordango check --target " + target.Id + "` says whether one app fits,");
                w.WriteLine("  and names every reason it does not.");
                w.WriteLine();
            }

            w.WriteLine(BuildConfig.Platform);
            w.WriteLine();
            w.WriteLine("    generates: nothing — the definition itself is what travels");
            w.WriteLine("    withholds: nothing — the platform runs the whole language");
            w.WriteLine("    needs:     a connection. `cordango login <token>` first.");
            w.WriteLine();
        });
    }

    private static JsonObject Describe(IAppSourceGenerator target) => new()
    {
        ["id"] = target.Id,
        ["version"] = target.Version,
        ["capabilities"] = new JsonObject(
            Axes(target.Capabilities).Select(a => new KeyValuePair<string, JsonNode?>(a.Axis, new JsonObject
            {
                ["supported"] = new JsonArray([.. a.Set.Supported.OrderBy(v => v, StringComparer.Ordinal)
                    .Select(v => (JsonNode)v)]),
                ["withheld"] = new JsonObject(a.Set.Withheld
                    .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value))),
            }))),
        ["seriesAndPrev"] = target.Capabilities.SeriesAndPrev,
        ["windowedRollups"] = target.Capabilities.WindowedRollups,
    };

    private static (string Axis, CapabilitySet Set)[] Axes(GeneratorCapabilities caps) =>
    [
        ("blocks", caps.Blocks),
        ("effects", caps.Effects),
        ("triggers", caps.Triggers),
        ("fieldTypes", caps.FieldTypes),
        ("platformApps", caps.PlatformTargets),
        ("platformEntities", caps.PlatformEntities),
    ];

    private static string Join(IEnumerable<string> values) =>
        string.Join(" ", values.OrderBy(v => v, StringComparer.Ordinal));
}

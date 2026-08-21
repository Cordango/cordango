// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json;
using System.Text.Json.Nodes;
using Cordango.Standalone.Security;

namespace Cordango.Standalone.Tests;

/// <summary>
/// The standalone permission rules, checked against the decision fixtures that Cordango Platform's
/// own suite also checks its implementation against.
///
/// <para>Two implementations of four rules, deliberately not sharing code, pinned to one set of
/// hand-written decisions. That is the whole arrangement: the platform never has to accept a change
/// made for standalone's reasons, and a divergence between them shows up as a red test in whichever
/// suite runs first rather than as a wrong answer in production.</para>
///
/// <para>If a case here fails, the interesting question is which side moved. Run the platform's
/// <c>PermissionFixtureTests</c> too: both failing means the fixture is wrong, one failing means an
/// implementation is.</para>
/// </summary>
public class PermissionFixtureTests
{
    public static TheoryData<string> Fixtures()
    {
        var data = new TheoryData<string>();
        foreach (var path in System.IO.Directory.EnumerateFiles(FixtureDirectory, "*.json").OrderBy(p => p, StringComparer.Ordinal))
            data.Add(Path.GetFileName(path));
        return data;
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Decisions_match_the_fixture(string fileName)
    {
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(FixtureDirectory, fileName)))!.AsObject();
        var permissions = FixtureRoles.Read(fixture["roles"]);
        var name = fixture["name"]?.GetValue<string>() ?? fileName;

        var cases = fixture["cases"]!.AsArray();
        Assert.NotEmpty(cases);

        for (var i = 0; i < cases.Count; i++)
        {
            var scenario = cases[i]!.AsObject();
            var roleKeys = scenario["roleKeys"]!.AsArray().Select(v => v!.GetValue<string>()).ToArray();
            var entity = scenario["entity"]!.GetValue<string>();
            var where = $"{fileName} case {i} ({name}), roles [{string.Join(", ", roleKeys)}] on '{entity}'";

            var access = PermissionResolver.Resolve(permissions, roleKeys, entity);

            var expect = scenario["expect"]!.AsObject();
            Assert.Equal(expect["create"]!.GetValue<bool>(), access.Create);
            Assert.Equal(expect["read"]!.GetValue<bool>(), access.Read);
            Assert.Equal(expect["update"]!.GetValue<bool>(), access.Update);
            Assert.Equal(expect["delete"]!.GetValue<bool>(), access.Delete);

            foreach (var (field, rule) in Pairs(scenario["fields"]))
            {
                var expected = rule!.AsObject();
                Assert.True(expected["read"]!.GetValue<bool>() == access.CanReadField(field),
                    $"{where}: field '{field}' read");
                Assert.True(expected["update"]!.GetValue<bool>() == access.CanUpdateField(field),
                    $"{where}: field '{field}' update");
            }

            foreach (var (command, allowed) in Pairs(scenario["commands"]))
            {
                Assert.True(allowed!.GetValue<bool>() == access.CanRunCommand(command),
                    $"{where}: command '{command}'");
            }
        }
    }

    /// <summary>
    /// Guards the guard. A directory that silently held no files, or a fixture that silently held no
    /// cases, would leave this class green while checking nothing — the failure mode every
    /// data-driven test has and the one nobody notices, because a passing suite looks the same
    /// either way.
    /// </summary>
    [Fact]
    public void The_fixtures_are_actually_there()
    {
        var files = System.IO.Directory.GetFiles(FixtureDirectory, "*.json");
        Assert.True(files.Length >= 6,
            $"Expected the permission decision fixtures in {FixtureDirectory}; found {files.Length} files. "
            + "They are the only thing keeping this implementation and the platform's from drifting apart.");
    }

    private static IEnumerable<(string Key, JsonNode? Value)> Pairs(JsonNode? node) =>
        node is JsonObject o ? o.Select(kv => (kv.Key, kv.Value)) : [];

    private static string FixtureDirectory =>
        Path.Combine(TestPaths.RepoRoot(), "tests", "fixtures", "permissions");
}

/// <summary>
/// Turns a definition's <c>roles</c> array into the typed <see cref="AppPermissions"/> the runtime
/// uses.
///
/// <para>It lives in the test project rather than in the runtime, and that is the point: a generated
/// application has no JSON roles to read. The generator emits <c>AppPermissions</c> as C#, compiled
/// in, so the runtime never parses a definition at all. This class is that translation done at test
/// time, so the fixtures can be written in the definition's own vocabulary.</para>
/// </summary>
internal static class FixtureRoles
{
    public static AppPermissions Read(JsonNode? roles)
    {
        if (roles is not JsonArray array) return AppPermissions.None;

        var definitions = new List<RoleDefinition>();
        foreach (var role in array.OfType<JsonObject>())
        {
            var key = role["key"]?.GetValue<string>();
            if (key is null) continue;

            var grants = new List<EntityGrant>();
            foreach (var grant in (role["grants"] as JsonArray)?.OfType<JsonObject>() ?? [])
            {
                var entity = grant["entity"]?.GetValue<string>();
                if (entity is null) continue;

                var overrides = new List<FieldOverride>();
                foreach (var o in (grant["fieldOverrides"] as JsonArray)?.OfType<JsonObject>() ?? [])
                    if (o["field"]?.GetValue<string>() is { } field)
                        overrides.Add(new FieldOverride(field, Bool(o["read"]), Bool(o["update"])));

                var commands = (grant["commands"] as JsonArray)?
                    .Select(c => c!.GetValue<string>())
                    .ToArray() ?? [];

                grants.Add(new EntityGrant(
                    entity,
                    Bool(grant["create"]) ?? false,
                    Bool(grant["read"]) ?? false,
                    Bool(grant["update"]) ?? false,
                    Bool(grant["delete"]) ?? false,
                    overrides,
                    commands));
            }

            definitions.Add(new RoleDefinition(key, grants));
        }

        return new AppPermissions(definitions);
    }

    /// <summary>Absent and null both mean "said nothing", which for a field override is different
    /// from having said false.</summary>
    private static bool? Bool(JsonNode? node) => node?.GetValueKind() switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };
}

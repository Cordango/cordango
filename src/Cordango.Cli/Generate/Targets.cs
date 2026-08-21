// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.SourceGen;
using Cordango.SourceGen.DotNetVue;

namespace Cordango.Cli.Generate;

/// <summary>
/// Which generators this build of the CLI can reach, and what <c>--target</c> may name.
///
/// <para>The registry lives HERE rather than in the SDK on purpose: the SDK defines what a generator
/// is and must not know that any particular one exists, or the first-party target becomes a special
/// case that every other target is measured against. The CLI is the composition root, so this is
/// the one place that knows the list.</para>
///
/// <para>An out-of-process generator — an executable that answers the protocol over stdin and
/// stdout — joins by being resolvable here too. Nothing above this class will need to change.</para>
/// </summary>
public static class Targets
{
    private static readonly IAppSourceGenerator[] Registered = [new DotNetVueGenerator()];

    /// <summary>
    /// What a user writes versus what a generator calls itself.
    ///
    /// <para><c>standalone</c> is the word for the IDEA — one application, yours, deployable
    /// anywhere — and <c>dotnet-vue</c> is one implementation of it. Somebody who wants a standalone
    /// application should not have to know which stack is the default one, and the day a second
    /// certified target exists, this alias is where the default is stated rather than a thing every
    /// user has to relearn.</para>
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> Aliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["standalone"] = "dotnet-vue",
        };

    public static IEnumerable<IAppSourceGenerator> All => Registered;

    public static IAppSourceGenerator? Find(string target)
    {
        var id = Aliases.TryGetValue(target, out var aliased) ? aliased : target;
        return Registered.FirstOrDefault(g => string.Equals(g.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Every name that resolves, for an error message that lists the alternatives instead
    /// of leaving somebody to guess the spelling.</summary>
    public static string Known =>
        string.Join(", ", Registered.Select(g => g.Id).Concat(Aliases.Keys).OrderBy(n => n, StringComparer.Ordinal));
}

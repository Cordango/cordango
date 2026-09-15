// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace Cordango.SourceGen.Common;

/// <summary>
/// The workspace an emitter is cutting: the applications that ship together, and the names the one
/// deployment around them is built from.
///
/// <para><b>The unit of a build is the workspace, not the application.</b> One database, one sign-in,
/// one directory, one shell — and inside it, apps that may reference and react to each other because
/// they were compiled together. A single application is a workspace of one; there is no second shape
/// for that case, because a code path that only runs when the count is one is a code path that
/// drifts.</para>
///
/// <para>This does no inference beyond the naming rules. Everything else an emitter needs is on the
/// <see cref="AppModel"/>s, which are unchanged by being plural.</para>
/// </summary>
public sealed class WorkspaceModel
{
    private WorkspaceModel(CompiledWorkspaceArtifact artifact)
    {
        Key = artifact.Identity.Key;
        Name = artifact.Identity.Name;
        Namespace = Naming.Pascal(Key);
        Apps = [.. artifact.Apps.Select(AppModel.From)];
    }

    public static WorkspaceModel From(CompiledWorkspaceArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        return new WorkspaceModel(artifact);
    }

    /// <summary>The workspace key: database name, cookie name, data-protection application name.</summary>
    public string Key { get; }

    /// <summary>The display name, as <c>cordango.yaml</c> spells it: "Finance".</summary>
    public string Name { get; }

    /// <summary>The C# namespace and assembly name for the host project: "Finance".</summary>
    public string Namespace { get; }

    /// <summary>
    /// The key as Docker will accept it: lower case, hyphens rather than underscores.
    ///
    /// <para>It names the COMPOSE PROJECT, and that is not cosmetic. Compose takes the project name
    /// from the directory it is run in unless the file says otherwise, and every workspace this
    /// generator produces is built into a directory somebody called <c>generated</c>. Two workspaces
    /// on one machine would otherwise be the same project: the same container names, the same
    /// network, and — the one that loses data — the same <c>generated_db</c> volume.</para>
    /// </summary>
    public string Slug => Key.Replace('_', '-').ToLowerInvariant();

    /// <summary>In the order <c>cordango.yaml</c> lists them. Load-bearing: it fixes the app
    /// switcher, the <c>ModelBuilder</c> calls and therefore the migration.</summary>
    public IReadOnlyList<AppModel> Apps { get; }

    /// <summary>The keys of the apps in this build, which is exactly the set a cross-app reference
    /// may name. An app outside it is a separately installed application.</summary>
    public IReadOnlySet<string> AppKeys =>
        Apps.Select(a => a.Key).ToHashSet(StringComparer.Ordinal);

    public AppModel? App(string key) => Apps.FirstOrDefault(a => a.Key == key);
}

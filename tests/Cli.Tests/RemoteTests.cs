// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.AccessTokens;
using Cordango.Cli.Remote;

namespace Cordango.Cli.Tests;

/// <summary>
/// The connected commands, up to the point where they would touch a socket.
///
/// <para>Everything asserted here happens BEFORE the first request — which target was resolved,
/// which credential was found, what is said when there is neither. That is deliberate: a test that
/// reached an instance would be testing the instance, and the failures worth locking down are the
/// ones a user hits on a laptop with nothing running.</para>
/// </summary>
public sealed class RemoteTests
{
    private const string Origin = "http://localhost:5215";

    [Fact]
    public void PublishWithoutALoginSaysSoAndSendsNothing()
    {
        using var cord = new Sandbox();
        cord.Run("new", "claims");

        var exit = cord.Run("publish");

        Assert.Equal(ExitCodes.NoInstance, exit);
        Assert.Contains("not connected to an instance", cord.Error);
        Assert.Contains("cordango login", cord.Error);
    }

    [Fact]
    public void PublishNamingAnInstanceItIsNotSignedIntoRefuses()
    {
        using var cord = new Sandbox();
        cord.Run("new", "claims");

        var exit = cord.Run("publish", "--instance", Origin);

        Assert.Equal(ExitCodes.NoInstance, exit);
        Assert.Contains("not signed in", cord.Error);
    }

    [Fact]
    public void LoginRefusesADamagedToken()
    {
        using var cord = new Sandbox();
        var raw = CordAccessToken.Issue(CordTokenKind.Exchange, Origin, "default").Encode();

        var exit = cord.Run("login", raw[..^3]);

        Assert.Equal(ExitCodes.Failed, exit);
        Assert.Contains("could not be read", cord.Error);
        // Nothing was written: a token that did not parse never became a stored credential.
        Assert.False(File.Exists(Credentials.Path));
    }

    [Fact]
    public void LoginRefusesAPersonalTokenWithNoAddress()
    {
        using var cord = new Sandbox();
        var raw = CordAccessToken.Issue(CordTokenKind.Personal).Encode();

        var exit = cord.Run("login", raw);

        Assert.Equal(ExitCodes.Failed, exit);
        Assert.Contains("--instance", cord.Error);
    }

    [Fact]
    public void ABoundWorkspacePicksItsOwnInstance()
    {
        using var cord = new Sandbox();
        cord.Run("new", "claims");

        var workspace = Workspace.WorkspaceFile.Find(cord.Root, out _);
        Assert.NotNull(workspace);

        var credentials = Credentials.Load();
        credentials.Save(new InstanceLogin(Origin, "cord_pat.a.b.c", "default", "t@example.com",
            DateTimeOffset.UtcNow));
        credentials.Bind(workspace!.WorkspaceId, Origin);
        credentials.Flush();

        // `whoami --offline` reports the binding without reaching anything, which is the one answer
        // available when the instance is down.
        var (exit, payload) = cord.RunJson("whoami", "--offline");

        Assert.Equal(ExitCodes.Ok, exit);
        Assert.Equal(Origin, (string?)payload["instance"]);
        Assert.Equal("default", (string?)payload["tenantId"]);
        Assert.False((bool?)payload["verified"]);
    }

    [Fact]
    public void LogoutForgetsTheCredentialAndTheBinding()
    {
        using var cord = new Sandbox();
        cord.Run("new", "claims");
        var workspace = Workspace.WorkspaceFile.Find(cord.Root, out _)!;

        var credentials = Credentials.Load();
        credentials.Save(new InstanceLogin(Origin, "cord_pat.a.b.c", "default", "t@example.com",
            DateTimeOffset.UtcNow));
        credentials.Bind(workspace.WorkspaceId, Origin);
        credentials.Flush();

        Assert.Equal(ExitCodes.Ok, cord.Run("logout"));

        var after = Credentials.Load();
        Assert.Null(after.Find(Origin));
        // The binding goes with it — a workspace pointing at an instance we hold no credential for
        // would produce "not signed in" on every publish instead of "not connected".
        Assert.Null(after.InstanceFor(workspace.WorkspaceId));
    }

    [Fact]
    public void LogoutOutsideAWorkspaceWithNoTargetRefusesRatherThanGuessing()
    {
        using var cord = new Sandbox();
        var credentials = Credentials.Load();
        credentials.Save(new InstanceLogin(Origin, "cord_pat.a.b.c", "default", "t@example.com",
            DateTimeOffset.UtcNow));
        credentials.Flush();

        var exit = cord.Run("logout");

        Assert.Equal(ExitCodes.Failed, exit);
        Assert.Contains("--all", cord.Error);
        // The credential survived a command that did not say which one to drop.
        Assert.NotNull(Credentials.Load().Find(Origin));
    }

    [Fact]
    public void CredentialsAreStoredOutsideTheWorkspace()
    {
        using var cord = new Sandbox();
        cord.Run("new", "claims");

        var credentials = Credentials.Load();
        credentials.Save(new InstanceLogin(Origin, "cord_pat.secret.value.check", "default", "t@example.com",
            DateTimeOffset.UtcNow));
        credentials.Flush();

        // The one thing that must never be true: a token inside the checkout, waiting to be
        // committed.
        var inRepo = Directory.EnumerateFiles(cord.Root, "*", SearchOption.AllDirectories)
            .Where(f => !f.StartsWith(cord.ConfigDirectory, StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("cord_pat.secret", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(inRepo);
        Assert.StartsWith(cord.ConfigDirectory, Credentials.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void OneInstanceHasOneSpellingHoweverItIsTyped()
    {
        using var cord = new Sandbox();
        var credentials = Credentials.Load();
        credentials.Save(new InstanceLogin(
            CordAccessToken.NormalizeOrigin("http://localhost:5215/"), "cord_pat.a.b.c", "default", "t",
            DateTimeOffset.UtcNow));
        credentials.Flush();

        // A trailing slash, a path, the bare origin — all one stored login, or `cordango publish` would
        // report "not signed in" to an instance the user demonstrably signed in to.
        var reloaded = Credentials.Load();
        Assert.NotNull(reloaded.Find("http://localhost:5215"));
        Assert.NotNull(reloaded.Find("http://localhost:5215/"));
        Assert.Single(reloaded.Origins);
    }
}

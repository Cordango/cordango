// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.AccessTokens;
using Cordango.Cli.Remote;

namespace Cordango.Cli.Tests;

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

        var reloaded = Credentials.Load();
        Assert.NotNull(reloaded.Find("http://localhost:5215"));
        Assert.NotNull(reloaded.Find("http://localhost:5215/"));
        Assert.Single(reloaded.Origins);
    }
}

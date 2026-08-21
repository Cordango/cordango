// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.AccessTokens;
using Cordango.Cli.Remote;
using Cordango.Cli.Workspace;

namespace Cordango.Cli.Commands;

/// <summary>
/// Connect this machine to a Cordango instance.
///
/// <para><b>Two spellings, and the difference is only how much you have to type.</b> An access
/// exchange token carries the instance address and tenant inside itself, so
/// <c>cordango login &lt;token&gt;</c> is the whole command. A personal access token does not, so it
/// needs <c>--instance</c> beside it. Both mint the same credential and both authenticate
/// identically; the exchange token exists because pasting one string is a better first experience
/// than pasting a string and a URL and getting the URL slightly wrong.</para>
///
/// <para><b>The token's self-description is checked, never believed.</b> An exchange token SAYS which
/// tenant it belongs to. Login connects to the named instance and asks the server which tenant it
/// actually belongs to, and refuses when they differ. Without that check the address inside the
/// token would be an assertion the CLI acts on, which is exactly the thing a pasted credential must
/// never be.</para>
/// </summary>
public static class LoginCommand
{
    public static async Task<int> RunAsync(Args args, Output output, CancellationToken ct)
    {
        // The token may be positional (`cordango login cord_cxt....`) or flagged (`--token`). Both, so
        // that a shell history containing a credential can be avoided by whoever cares to.
        var raw = args.Value("token") ?? args.First;
        if (string.IsNullOrWhiteSpace(raw))
            return output.Usage(
                "cordango login <token> [--instance <url>]\n"
                + "  Create a token under your avatar menu → Personal Access Keys.\n"
                + "  An access exchange token carries its instance address; a personal access token needs --instance.");

        if (!CordAccessToken.TryParse(raw, out var token, out var parseError) || token is null)
            return output.Fail("that token could not be read", [parseError ?? "unrecognised token"]);

        // Precedence: an explicit --instance wins, because somebody typing it has a reason (a tunnel,
        // a hostname the server does not know it answers on). Otherwise the exchange token's own
        // address. A personal token with neither has nowhere to go.
        var origin = args.Value("instance") ?? token.InstanceUrl;
        if (string.IsNullOrWhiteSpace(origin))
            return output.Fail("this personal access token does not say which instance it belongs to",
                ["Add --instance <url>, or create an access exchange token instead — it carries the address."]);

        origin = CordAccessToken.NormalizeOrigin(origin);

        using var instance = new Instance(origin, raw);
        var result = await instance.WhoAmIAsync(ct);
        if (!result.Ok) return output.Fail($"could not sign in to {origin}", result.Errors, code: ExitCodes.NoInstance);

        var tenantId = (string?)result.Body?["tenantId"] ?? "";
        var tenantName = (string?)result.Body?["tenantName"] ?? tenantId;
        var user = (string?)result.Body?["email"] ?? (string?)result.Body?["name"] ?? "";

        // THE check the exchange format exists to make possible. The server just told us which
        // workspace this key really belongs to; if the token described itself differently, it is
        // either damaged or was minted somewhere it did not come from, and neither is a login.
        if (token.TenantId is { Length: > 0 } claimed
            && !string.Equals(claimed, tenantId, StringComparison.Ordinal))
        {
            return output.Fail("this token does not belong to that instance",
                [$"the token names workspace '{claimed}', but {origin} says the key belongs to '{tenantId}'"]);
        }

        var credentials = Credentials.Load();
        credentials.Save(new InstanceLogin(origin, raw, tenantId, user, DateTimeOffset.UtcNow));

        // Bind the workspace we are standing in, if we are standing in one — so `cordango publish` from
        // this checkout needs no arguments. Running login from anywhere else is fine and simply
        // stores the credential.
        var workspace = WorkspaceFile.Find(Directory.GetCurrentDirectory(), out _);
        if (workspace is { WorkspaceId.Length: > 0 }) credentials.Bind(workspace.WorkspaceId, origin);

        credentials.Flush();

        return output.Ok(
            new JsonObject
            {
                ["instance"] = origin,
                ["tenantId"] = tenantId,
                ["tenantName"] = tenantName,
                ["user"] = user,
                ["kind"] = token.Kind == CordTokenKind.Exchange ? "exchange" : "pat",
                ["boundWorkspace"] = workspace?.WorkspaceId,
                ["credentials"] = Credentials.Path,
            },
            w =>
            {
                w.WriteLine($"Signed in to {origin}");
                w.WriteLine($"  workspace   {tenantName} ({tenantId})");
                w.WriteLine($"  as          {user}");
                if (workspace is { WorkspaceId.Length: > 0 })
                    w.WriteLine($"  bound       {workspace.Name} → this instance");
                w.WriteLine();
                w.WriteLine($"Credential stored in {Credentials.Path}");
            });
    }
}

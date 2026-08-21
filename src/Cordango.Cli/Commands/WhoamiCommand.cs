// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Cli.Remote;
using Cordango.Cli.Workspace;

namespace Cordango.Cli.Commands;

/// <summary>
/// Which instance would <c>cordango publish</c> write to, and as whom.
///
/// <para><b>It asks the server rather than reading the file.</b> The stored login records what was
/// true at <c>cordango login</c>; a key revoked yesterday still sits there looking perfectly valid. The
/// only useful answer to "am I connected" is one the instance just gave, so this makes the call.
/// <c>--offline</c> prints what is stored, for when the instance is down and the question is merely
/// which address is bound.</para>
/// </summary>
public static class WhoamiCommand
{
    public static async Task<int> RunAsync(Args args, Output output, CancellationToken ct)
    {
        var credentials = Credentials.Load();
        var workspace = WorkspaceFile.Find(Directory.GetCurrentDirectory(), out _);

        var origin = args.Value("instance")
            ?? (workspace is { WorkspaceId.Length: > 0 } ? credentials.InstanceFor(workspace.WorkspaceId) : null);

        if (origin is null)
        {
            return output.Fail("not connected to an instance",
            [
                workspace is null
                    ? "Run this inside a workspace, or name an instance with --instance <url>."
                    : "Run `cordango login <token>` in this workspace to connect it.",
                $"stored logins: {(credentials.Origins.Any() ? string.Join(", ", credentials.Origins) : "(none)")}",
            ], code: ExitCodes.NoInstance);
        }

        if (credentials.Find(origin) is not { Token.Length: > 0 } login)
            return output.Fail($"not signed in to {origin}", [$"Run `cordango login <token> --instance {origin}`."], code: ExitCodes.NoInstance);

        if (args.Has("offline"))
        {
            return output.Ok(
                Describe(origin, login.TenantId, login.User, workspace, verified: false),
                w =>
                {
                    w.WriteLine($"{origin}  (stored, not verified)");
                    w.WriteLine($"  workspace   {login.TenantId}");
                    w.WriteLine($"  as          {login.User}");
                });
        }

        using var instance = new Instance(origin, login.Token);
        var result = await instance.WhoAmIAsync(ct);
        if (!result.Ok) return output.Fail($"could not reach {origin} as this user", result.Errors, code: ExitCodes.NoInstance);

        var tenantId = (string?)result.Body?["tenantId"] ?? login.TenantId;
        var tenantName = (string?)result.Body?["tenantName"] ?? tenantId;
        var user = (string?)result.Body?["email"] ?? login.User;
        var role = (string?)result.Body?["platformRole"] ?? "";

        var payload = Describe(origin, tenantId, user, workspace, verified: true);
        payload["tenantName"] = tenantName;
        payload["platformRole"] = role;

        return output.Ok(payload, w =>
        {
            w.WriteLine(origin);
            w.WriteLine($"  workspace   {tenantName} ({tenantId})");
            w.WriteLine($"  as          {user}{(role.Length > 0 ? $" ({role})" : "")}");
            if (workspace is not null)
                w.WriteLine($"  publishing  {workspace.Name} — {workspace.Apps.Count} app(s)");
        });
    }

    private static JsonObject Describe(string origin, string tenantId, string user,
        WorkspaceFile? workspace, bool verified) => new()
        {
            ["instance"] = origin,
            ["tenantId"] = tenantId,
            ["user"] = user,
            ["verified"] = verified,
            ["workspace"] = workspace is null ? null : new JsonObject
            {
                ["id"] = workspace.WorkspaceId,
                ["name"] = workspace.Name,
                ["apps"] = new JsonArray([.. workspace.Apps.Select(a => (JsonNode)a!)]),
            },
        };
}

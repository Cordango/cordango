// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Cordango.Cli.Remote;

/// <summary>What the instance answered. <paramref name="Errors"/> is always a list, even for one —
/// same rule as <see cref="Output.Fail"/>, and for the same reason.</summary>
public sealed record InstanceResult(bool Ok, HttpStatusCode Status, JsonObject? Body, IReadOnlyList<string> Errors)
{
    public static InstanceResult Failed(HttpStatusCode status, params string[] errors) =>
        new(false, status, null, errors);
}

/// <summary>
/// A Cordango instance, over HTTP.
///
/// <para><b>The CLI's only network surface.</b> Everything else in <c>cordango</c> is offline and
/// token-free by design (CordyOSS §14: "no model call in new, doctor, check, build, run or test"),
/// so the reachable-instance code is confined to one class that only <c>login</c>, <c>whoami</c> and
/// <c>publish</c> construct. Nothing on the <c>check</c>/<c>build</c> path may ever import it.</para>
///
/// <para><b>It speaks the platform's ordinary API.</b> There is no CLI-specific endpoint: publishing
/// hits the same route Studio would, under the same authorization. A separate machine API would be
/// free to drift from the one humans use, and the drift would land on whoever automated first.</para>
/// </summary>
public sealed class Instance : IDisposable
{
    private readonly HttpClient _http;

    public Instance(string origin, string? token)
    {
        Origin = Cordango.AccessTokens.CordAccessToken.NormalizeOrigin(origin);
        _http = new HttpClient { BaseAddress = new Uri(Origin + "/"), Timeout = TimeSpan.FromMinutes(5) };
        if (token is { Length: > 0 })
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        // Publishing a real app compiles a definition and provisions tables; five minutes is the
        // timeout for THAT, not for a health check.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("cordango-cli/" + Commands.VersionCommand.CliVersion);
    }

    public string Origin { get; }

    /// <summary>Who this token acts as, and where. The login check, and the whole of `cordango whoami`.</summary>
    public Task<InstanceResult> WhoAmIAsync(CancellationToken ct) => SendAsync(
        () => new HttpRequestMessage(HttpMethod.Get, "api/me"), ct);

    /// <summary>Publish one built App Definition and make it live.</summary>
    public Task<InstanceResult> PublishAsync(JsonNode definition, bool force, CancellationToken ct) => SendAsync(
        () => new HttpRequestMessage(HttpMethod.Post, "api/apps/publish")
        {
            Content = JsonContent.Create(new JsonObject
            {
                ["definition"] = definition.DeepClone(),
                ["force"] = force,
            }),
        }, ct);

    private async Task<InstanceResult> SendAsync(Func<HttpRequestMessage> request, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request(), ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // "Connection refused" against localhost:5215 is by far the most common failure here and
            // it is not a bug — the instance is not running. Say the address, so the answer is
            // visible without a second command.
            return InstanceResult.Failed(0, $"could not reach {Origin}: {ex.Message}");
        }

        JsonObject? body = null;
        try
        {
            if (await response.Content.ReadAsStringAsync(ct) is { Length: > 0 } text)
                body = JsonNode.Parse(text) as JsonObject;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException)
        {
            // A non-JSON body from a proxy or an error page. The status still says enough.
        }

        if (response.IsSuccessStatusCode) return new InstanceResult(true, response.StatusCode, body, []);

        return new InstanceResult(false, response.StatusCode, body, Explain(response.StatusCode, body));
    }

    /// <summary>
    /// The server's refusal, in the CLI's voice.
    ///
    /// <para>The platform answers <c>{ code, error }</c> — the code is stable and the error is
    /// already in the caller's language (see the localization contract), so this passes the message
    /// through rather than inventing an English one beside it. Gate failures arrive as
    /// <c>errors: []</c> instead and are passed through whole: a definition that failed validation
    /// server-side needs every reason, not the first.</para>
    /// </summary>
    private static IReadOnlyList<string> Explain(HttpStatusCode status, JsonObject? body)
    {
        if (body?["errors"] is JsonArray list && list.Count > 0)
            return [.. list.Select(e => e?.ToString() ?? "").Where(e => e.Length > 0)];

        if ((string?)body?["error"] is { Length: > 0 } message) return [message];

        return status switch
        {
            HttpStatusCode.Unauthorized =>
                ["the instance rejected this credential — run `cordango login` again"],
            HttpStatusCode.Forbidden =>
                ["this account may not perform that action on this instance"],
            HttpStatusCode.NotFound =>
                ["the instance has no such endpoint — it is probably older than this cordango"],
            _ => [$"the instance answered {(int)status} {status}"],
        };
    }

    public void Dispose() => _http.Dispose();
}

// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cordango.Standalone.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Cordango.Standalone.Mcp;

/// <summary>
/// The third face of a generated application: an MCP endpoint beside its UI and its REST routes.
///
/// <para><b>What makes it safe to have on by default.</b> It is not an integration with a permission
/// model of its own. Every tool projects an operation that already exists, through the same
/// <see cref="IRecordGateway"/> the controllers go through, so an AI client acting as somebody
/// reaches exactly what that person reaches — no more, and refused in the same words.</para>
///
/// <para><b>Stateless, and that is free rather than a compromise.</b> Protocol revision 2026-07-28
/// removed the <c>initialize</c> handshake and connection-scoped sessions: each request carries its
/// own protocol version and client identity. So there is no session store to run, and an application
/// behind a round-robin proxy or scaled to three containers needs nothing extra.</para>
/// </summary>
public static class McpModule
{
    /// <summary>The endpoint path. Outside <c>/api</c> deliberately: the catch-all that turns an
    /// unclaimed API route into a <c>{code, error}</c> 404 would otherwise swallow it.</summary>
    public const string Path = "/mcp";

    /// <summary>
    /// Register the server and its tools.
    ///
    /// <para>The tools are resolved per call, so each one sees the CALLER's permissions rather than
    /// whatever the first request through the process happened to have.</para>
    /// </summary>
    /// <param name="schema">This application's catalogue. Passed rather than resolved because the
    /// tool schemas are built ONCE, here, at startup — the list of entities is baked into them, so
    /// there is nothing per-request to look up.</param>
    public static IServiceCollection AddCordangoMcp(this IServiceCollection services, AppSchemaCatalogue schema)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(schema);

        services.AddScoped<CordangoTools>();

        services.AddMcpServer(options =>
            {
                options.ServerInfo = new ModelContextProtocol.Protocol.Implementation
                {
                    Name = schema.AppKey,
                    Title = schema.AppName,
                    Version = schema.AppVersion,
                };
            })
            .WithHttpTransport(transport => transport.Stateless = true)
            .WithTools(Tools(schema));

        return services;
    }

    /// <summary>
    /// The tools, built from <see cref="CordangoTools"/>'s annotated methods.
    ///
    /// <para>Built rather than discovered with <c>WithTools&lt;T&gt;()</c> for one reason: that
    /// overload gives no way to reach the schema, and the schema is where the list of THIS
    /// application's entities has to go.</para>
    /// </summary>
    private static IEnumerable<McpServerTool> Tools(AppSchemaCatalogue schema)
    {
        var keys = new JsonArray([.. schema.EntityKeys.Select(k => (JsonNode)JsonValue.Create(k))]);

        foreach (var method in typeof(CordangoTools).GetMethods())
        {
            if (method.GetCustomAttribute<McpServerToolAttribute>() is null) continue;

            var tool = McpServerTool.Create(
                method,
                request => request.Services!.GetRequiredService<CordangoTools>(),
                new McpServerToolCreateOptions());

            Constrain(tool.ProtocolTool, keys);
            yield return tool;
        }
    }

    /// <summary>
    /// Give a tool's <c>entity</c> parameter the list of entities this application actually has.
    ///
    /// <para>A model that can read the valid keys off the tool's own schema does not need a
    /// <c>describe_app</c> round trip before its first call, and cannot invent a plural. Done once at
    /// startup, so there is no per-request work here.</para>
    ///
    /// <para>Written onto the finished schema rather than through the schema generator's own
    /// transform hook. The hook is the tidier-looking route and it did not fire for a parameter's
    /// root node, which is a silent failure: the tools still work, the enum is simply absent, and
    /// nothing says so. Rewriting the finished document is verifiable by looking at it.</para>
    /// </summary>
    private static void Constrain(Tool tool, JsonArray keys)
    {
        if (JsonNode.Parse(tool.InputSchema.GetRawText()) is not JsonObject schema) return;
        if (schema["properties"] is not JsonObject properties) return;
        if (properties["entity"] is not JsonObject entity) return;

        entity["enum"] = keys.DeepClone();
        tool.InputSchema = JsonSerializer.Deserialize<JsonElement>(schema.ToJsonString());
    }

    /// <summary>
    /// Put the endpoint in the route table.
    ///
    /// <para>Map it AFTER authentication is in the pipeline and BEFORE the single-page fallback: a
    /// real endpoint beats the fallback, but only if it is there to be matched.</para>
    /// </summary>
    public static IEndpointConventionBuilder MapCordangoMcp(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapServerCard();

        // An MCP client is not a browser: it has no session cookie to forge and no way to carry the
        // antiforgery token that every other unsafe verb in this application requires. The global
        // AntiforgeryFilter honours this attribute for exactly that case. It is only sound because
        // the credential an MCP caller uses is a bearer token, which is not attached to a request by
        // a browser on some other site's behalf — the thing antiforgery exists to stop.
        return endpoints.MapMcp(Path)
            .WithMetadata(new Microsoft.AspNetCore.Mvc.IgnoreAntiforgeryTokenAttribute());
    }

    /// <summary>
    /// A server card, at the path the discovery proposal currently names.
    ///
    /// <para><b>Provisional, on purpose.</b> Discovery is not in the 2026-07-28 specification — it is
    /// SEP-2127, still an open proposal, and the path it proposes is
    /// <c>/.well-known/mcp/server-card.json</c> rather than the <c>/.well-known/mcp.json</c> that
    /// most write-ups say. Serving it costs nothing and helps a client that already looks; nothing in
    /// this application depends on it, and it is expected to move.</para>
    ///
    /// <para>It deliberately does NOT list tools. The proposal omits them for the reason that applies
    /// here twice over: a static document cannot represent a surface that changes, and this one's
    /// answers depend on who is asking.</para>
    /// </summary>
    private static void MapServerCard(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/.well-known/mcp/server-card.json", (HttpContext context) =>
        {
            var schema = context.RequestServices.GetService<AppSchemaCatalogue>();
            var origin = $"{context.Request.Scheme}://{context.Request.Host}";

            return Results.Json(new JsonObject
            {
                ["name"] = $"io.cordango/{schema?.AppKey ?? "app"}",
                ["title"] = schema?.AppName ?? "Cordango application",
                ["description"] = schema?.Description,
                ["version"] = schema?.AppVersion,
                ["remotes"] = new JsonArray(new JsonObject
                {
                    ["type"] = "streamable-http",
                    ["url"] = origin + Path,
                }),
                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            });
        }).AllowAnonymous();
}

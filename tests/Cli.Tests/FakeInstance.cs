// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Cordango.Cli.Tests;

/// <summary>
/// A Cordango instance, as far as the CLI can tell.
///
/// <para>The remote half of <c>import</c> is two HTTP calls and a shape agreement, and neither is
/// testable by asserting on a refusal. This answers the two routes the command uses, in the casing
/// the platform's <c>JsonSerializerDefaults.Web</c> produces — so a rename on either side shows up
/// here rather than the first time somebody runs it against a real instance.</para>
///
/// <para><c>HttpListener</c> on a <c>localhost</c> prefix needs no elevation and no URL reservation
/// on any of the three platforms this ships to.</para>
/// </summary>
public sealed class FakeInstance : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Dictionary<string, JsonObject> _definitions = new(StringComparer.Ordinal);
    private readonly JsonArray _apps = [];
    private readonly CancellationTokenSource _stopping = new();

    public FakeInstance()
    {
        Origin = $"http://localhost:{FreePort()}";
        _listener.Prefixes.Add(Origin + "/");
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    public string Origin { get; }

    public int ListCalls { get; private set; }

    public FakeInstance With(string id, string handle, string name, JsonObject? definition,
        string status = "built", string version = "1.0.0", int entities = 3)
    {
        _apps.Add(new JsonObject
        {
            ["id"] = id,
            ["handle"] = handle,
            ["name"] = name,
            ["status"] = status,
            ["version"] = version,
            ["visibility"] = "personal",
            ["entities"] = entities,
        });

        if (definition is not null) _definitions[id] = definition;
        return this;
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task ServeAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                return;
            }

            try
            {
                Answer(context);
            }
            catch (Exception ex) when (ex is HttpListenerException or IOException)
            {
            }
        }
    }

    private void Answer(HttpListenerContext context)
    {
        var path = context.Request.Url?.AbsolutePath ?? "";

        if (path is "/api/apps")
        {
            ListCalls++;
            Write(context, 200, _apps);
            return;
        }

        if (path.StartsWith("/api/apps/", StringComparison.Ordinal))
        {
            var id = Uri.UnescapeDataString(path["/api/apps/".Length..]);
            var row = _apps.OfType<JsonObject>().FirstOrDefault(a => (string?)a["id"] == id);

            if (row is null)
            {
                Write(context, 404, new JsonObject { ["error"] = "no such app" });
                return;
            }

            var body = (JsonObject)row.DeepClone();
            if (_definitions.TryGetValue(id, out var definition)) body["definition"] = definition.DeepClone();
            Write(context, 200, body);
            return;
        }

        Write(context, 404, new JsonObject { ["error"] = "no such endpoint" });
    }

    private static void Write(HttpListenerContext context, int status, JsonNode body)
    {
        var bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes);
        context.Response.Close();
    }

    public void Dispose()
    {
        _stopping.Cancel();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
        }

        _stopping.Dispose();
    }
}

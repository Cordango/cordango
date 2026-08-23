// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Cordango.Standalone.Data;
using Cordango.Standalone.Hosting;
using Cordango.Standalone.Mcp;
using Cordango.Standalone.Records;
using Cordango.Standalone.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cordango.Standalone.Tests;

public class McpServerTests : IAsyncLifetime
{
    private const string Protocol = "2026-07-28";

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();

        builder.Services.AddDbContext<WidgetDb>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        builder.Services.AddScoped<CordangoDbContext>(s => s.GetRequiredService<WidgetDb>());
        builder.Services.AddCordangoRuntime();
        builder.Services.AddSingleton(McpFixture.Permissions);
        builder.Services.AddSingleton<ICurrentUser>(new McpFixture.Reader());
        builder.Services.AddSingleton(McpFixture.Catalogue());
        builder.Services.AddRecord(McpFixture.Descriptor);
        builder.Services.AddCordangoMcp(McpFixture.Catalogue());

        _app = builder.Build();
        _app.MapCordangoMcp();
        _app.Urls.Add("http://127.0.0.1:0");

        await _app.StartAsync();

        _client = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    /// <summary>One JSON-RPC request, carrying the headers the transport requires of every POST:
    /// the protocol version, the method, and the name of the thing being addressed.</summary>
    private async Task<JsonNode> CallAsync(string method, JsonObject? parameters = null, string? name = null)
    {
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = method,
            ["params"] = parameters ?? new JsonObject(),
        };

        body["params"]!["_meta"] = new JsonObject
        {
            ["io.modelcontextprotocol/protocolVersion"] = Protocol,
            ["io.modelcontextprotocol/clientInfo"] = new JsonObject
            {
                ["name"] = "Cordango.Standalone.Tests",
                ["version"] = "1.0.0",
            },
            ["io.modelcontextprotocol/clientCapabilities"] = new JsonObject(),
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, McpModule.Path)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        request.Headers.Add("MCP-Protocol-Version", Protocol);
        request.Headers.Add("Mcp-Method", method);
        if (name is not null) request.Headers.Add("Mcp-Name", name);

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();

        // A response may arrive as a single JSON object or as an SSE stream carrying one; both are
        // legal for a request, and which one the server picks is its choice rather than ours.
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            text = text.Split('\n').First(l => l.StartsWith("data:", StringComparison.Ordinal))["data:".Length..];

        return JsonNode.Parse(text)!;
    }

    private async Task<JsonNode> ToolAsync(string tool, JsonObject arguments)
    {
        var response = await CallAsync(
            "tools/call", new JsonObject { ["name"] = tool, ["arguments"] = arguments }, tool);

        return response["result"] ?? response["error"]!;
    }

    /// <summary>The text a tool answered with, parsed. The protocol carries a result as content
    /// blocks, so a JSON answer arrives as a string inside one.</summary>
    private static JsonNode Payload(JsonNode result) =>
        JsonNode.Parse(result["content"]![0]!["text"]!.GetValue<string>())!;

    [Fact]
    public async Task The_tool_list_is_eight_tools_whatever_the_application_contains()
    {
        var response = await CallAsync("tools/list");
        var names = response["result"]!["tools"]!.AsArray()
            .Select(t => t!["name"]!.GetValue<string>()).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(
            ["aggregate_records", "create_record", "delete_record", "describe_app",
             "get_record", "list_records", "run_command", "update_record"],
            names);
    }

    [Fact]
    public async Task A_tool_naming_an_entity_offers_the_ones_this_application_has()
    {
        var response = await CallAsync("tools/list");
        var list = response["result"]!["tools"]!.AsArray()
            .First(t => t!["name"]!.GetValue<string>() == "list_records");

        var choices = list!["inputSchema"]!["properties"]!["entity"]!["enum"]!
            .AsArray().Select(v => v!.GetValue<string>());

        Assert.Equal(["widget"], choices);
    }

    [Fact]
    public async Task Describe_app_reports_the_fields_and_what_this_caller_may_do()
    {
        var described = Payload(await ToolAsync("describe_app", []));
        var entity = described["entities"]!.AsArray().Single();

        Assert.Equal("widget", entity["key"]!.GetValue<string>());
        Assert.Equal("number", entity["fields"]!["properties"]!["amount"]!["type"]!.GetValue<string>());

        // The fixture's role may read and may not write, so an MCP client is TOLD that up front
        // rather than finding out by being refused halfway through a task.
        Assert.True(entity["youMay"]!["read"]!.GetValue<bool>());
        Assert.False(entity["youMay"]!["create"]!.GetValue<bool>());
    }

    [Fact]
    public async Task An_unknown_entity_is_answered_by_naming_the_ones_that_exist()
    {
        var result = await ToolAsync("list_records", new JsonObject { ["entity"] = "widgets" });
        var text = result.ToJsonString();

        Assert.Contains("widgets", text, StringComparison.Ordinal);
        Assert.Contains("widget", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_write_the_callers_role_forbids_is_refused_with_the_same_code_the_api_uses()
    {
        var result = await ToolAsync("create_record", new JsonObject
        {
            ["entity"] = "widget",
            ["values"] = new JsonObject { ["name"] = "Anything" },
        });

        Assert.Contains("record.create_denied", result.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Listing_goes_through_the_gateway_and_returns_the_paging_envelope()
    {
        var listed = Payload(await ToolAsync("list_records", new JsonObject { ["entity"] = "widget" }));

        Assert.Equal(0, listed["total"]!.GetValue<int>());
        Assert.Equal(50, listed["take"]!.GetValue<int>());
        Assert.Empty(listed["items"]!.AsArray());
    }

    [Fact]
    public async Task A_request_whose_header_contradicts_its_body_is_refused()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, McpModule.Path)
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""",
                Encoding.UTF8,
                "application/json"),
        };

        request.Headers.Add("MCP-Protocol-Version", Protocol);
        request.Headers.Add("Mcp-Method", "resources/list");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_server_card_says_where_the_endpoint_is()
    {
        var card = JsonNode.Parse(await _client.GetStringAsync("/.well-known/mcp/server-card.json"))!;

        Assert.Equal("Widgets", card["title"]!.GetValue<string>());
        Assert.EndsWith("/mcp", card["remotes"]![0]!["url"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("streamable-http", card["remotes"]![0]!["type"]!.GetValue<string>());
    }
}

internal static class McpFixture
{
    /// <summary>A role that may read widgets and may not write them, so one suite covers both the
    /// allowed and the refused path.</summary>
    public static readonly AppPermissions Permissions =
        new([new RoleDefinition("reader", [new EntityGrant("widget", Read: true)])]);

    public sealed class Reader : ICurrentUser
    {
        public string? UserId => "reader-1";
        public string? PersonId => "person-1";
        public IReadOnlyCollection<string> RoleKeys => ["reader"];
        public bool IsAdministrator => false;
    }

    public static readonly RecordDescriptor<Widget> Descriptor = new("widget", "Widget",
        [new RecordField<Widget>("name", nameof(Widget.Name), (from, to) => to.Name = from.Name)]);

    public static AppSchemaCatalogue Catalogue() => new(
        "widgets", "Widgets", "2.1.0", "A test application.",
        [
            new EntitySchema("widget", "Widget", "Widgets", "name",
                """{"type":"object","properties":{"id":{"type":"string"},"name":{"type":"string"},"amount":{"type":"number"}}}""",
                """{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}""",
                """{"type":"object","properties":{"name":{"type":"string"}}}"""),
        ],
        []);
}

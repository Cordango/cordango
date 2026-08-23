// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Standalone.Data;
using Cordango.Standalone.Hosting;
using Cordango.Standalone.Http;
using Cordango.Standalone.Records;
using Cordango.Standalone.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cordango.Standalone.Tests;

public class OpenApiDocumentTests
{
    private const string ReadSchema =
        """
        {"type":"object","properties":{"id":{"type":"string","title":"ID"},
        "name":{"type":"string","title":"Name"},
        "status":{"type":"string","enum":["open","closed"],"title":"Status"},
        "amount":{"type":"number","title":"Amount","description":"In EUR."}},
        "additionalProperties":false}
        """;

    private const string CreateSchema =
        """
        {"type":"object","properties":{"name":{"type":"string","title":"Name"},
        "status":{"type":"string","enum":["open","closed"],"title":"Status"},
        "amount":{"type":"number","title":"Amount"}},
        "required":["name"],"additionalProperties":false}
        """;

    private const string UpdateSchema =
        """
        {"type":"object","properties":{"name":{"type":"string","title":"Name"}},
        "additionalProperties":false}
        """;

    private static AppSchemaCatalogue Catalogue() => new(
        "widgets", "Widgets", "2.1.0", "A test application.",
        [new EntitySchema("widget", "Widget", "Widgets", "name", ReadSchema, CreateSchema, UpdateSchema)],
        [new CommandSchema("close_widget", "Close", "widget",
            """{"type":"object","properties":{"reason":{"type":"string"}},"required":["reason"]}""")]);

    private static async Task<JsonNode> DocumentAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();

        builder.Services.AddDbContext<WidgetDb>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        builder.Services.AddScoped<CordangoDbContext>(s => s.GetRequiredService<WidgetDb>());
        builder.Services.AddCordangoRuntime();
        builder.Services.AddSingleton(AppPermissions.None);
        builder.Services.AddSingleton(Catalogue());
        builder.Services.AddRecord(new RecordDescriptor<Widget>("widget", "Widget",
            [new RecordField<Widget>("name", nameof(Widget.Name), (from, to) => to.Name = from.Name)]));

        builder.Services.AddControllers().AddApplicationPart(typeof(WidgetController).Assembly);
        builder.Services.AddOpenApi(o => o.AddDocumentTransformer<OpenApiFromSchema>());

        await using var app = builder.Build();
        app.MapControllers();
        app.MapOpenApi();

        // Port 0: the OS picks a free one, so a run of this suite never collides with whatever else
        // is listening on the machine.
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();

        var address = app.Urls.First();
        using var client = new HttpClient();
        var json = await client.GetStringAsync($"{address}/openapi/v1.json");

        await app.StopAsync();

        return JsonNode.Parse(json)!;
    }

    [Fact]
    public async Task The_document_is_served_and_names_the_application()
    {
        var document = await DocumentAsync();

        Assert.Equal("Widgets API", document["info"]!["title"]!.GetValue<string>());
        Assert.Equal("2.1.0", document["info"]!["version"]!.GetValue<string>());
        Assert.Contains("A test application.", document["info"]!["description"]!.GetValue<string>());
    }

    [Fact]
    public async Task Every_field_carries_its_real_type_rather_than_object()
    {
        var document = await DocumentAsync();
        var properties = document["components"]!["schemas"]!["widget"]!["properties"]!;

        Assert.Equal("string", properties["name"]!["type"]!.GetValue<string>());
        Assert.Equal("number", properties["amount"]!["type"]!.GetValue<string>());
        Assert.Equal("In EUR.", properties["amount"]!["description"]!.GetValue<string>());

        var choices = properties["status"]!["enum"]!.AsArray().Select(v => v!.GetValue<string>());
        Assert.Equal(["open", "closed"], choices);
    }

    [Fact]
    public async Task Creating_and_patching_are_described_by_different_schemas()
    {
        var document = await DocumentAsync();
        var schemas = document["components"]!["schemas"]!;

        Assert.Equal(["name"], schemas["widget_create"]!["required"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.Null(schemas["widget_update"]!["required"]);

        // The id is a column the runtime owns, so a create body that could name it would be a lie
        // about what the route accepts.
        Assert.Null(schemas["widget_create"]!["properties"]!["id"]);
        Assert.NotNull(schemas["widget"]!["properties"]!["id"]);
    }

    [Fact]
    public async Task The_write_routes_point_at_the_write_schemas()
    {
        var document = await DocumentAsync();
        var path = document["paths"]!["/api/widget"]!;

        var body = path["post"]!["requestBody"]!["content"]!["application/json"]!["schema"]!;
        Assert.Equal("#/components/schemas/widget_create", body["$ref"]!.GetValue<string>());

        var patch = document["paths"]!["/api/widget/{id}"]!["patch"]!
            ["requestBody"]!["content"]!["application/json"]!["schema"]!;
        Assert.Equal("#/components/schemas/widget_update", patch["$ref"]!.GetValue<string>());
    }

    [Fact]
    public async Task Listing_describes_its_envelope_and_its_query_parameters()
    {
        var document = await DocumentAsync();
        var list = document["paths"]!["/api/widget"]!["get"]!;

        var named = list["parameters"]!.AsArray().Select(p => p!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(["filter", "sort", "skip", "take"], named);

        var envelope = list["responses"]!["200"]!["content"]!["application/json"]!["schema"]!["properties"]!;
        Assert.Equal("#/components/schemas/widget", envelope["items"]!["items"]!["$ref"]!.GetValue<string>());
        Assert.Equal("integer", envelope["total"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_command_contributes_its_input_schema_to_the_route_that_runs_it()
    {
        var document = await DocumentAsync();
        var run = document["paths"]!["/api/widget/{id}/commands/{command}"]!["post"]!;

        Assert.Contains("close_widget", run["description"]!.GetValue<string>());

        var options = run["requestBody"]!["content"]!["application/json"]!["schema"]!["oneOf"]!.AsArray();
        Assert.Single(options);
        Assert.Equal("close_widget", options[0]!["title"]!.GetValue<string>());
        Assert.Equal(["reason"], options[0]!["required"]!.AsArray().Select(v => v!.GetValue<string>()));
    }

    [Fact]
    public async Task A_route_the_application_does_not_serve_is_not_advertised()
    {
        var document = await DocumentAsync();

        // Nothing registered an entity keyed 'invoice', so nothing may claim one exists.
        Assert.Null(document["paths"]!["/api/invoice"]);
    }
}

public sealed class Widget : IRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class WidgetDb : CordangoDbContext
{
    public WidgetDb(DbContextOptions<WidgetDb> options, ICurrentUser user, IClock clock)
        : base(options, user, clock) { }

    protected override void ConfigureModel(ModelBuilder builder) =>
        builder.Entity<Widget>().HasKey(e => e.Id);
}

[Route("api/widget")]
public sealed class WidgetController : RecordsController<Widget>
{
    public WidgetController(RecordGateway<Widget> records) : base(records) { }
}

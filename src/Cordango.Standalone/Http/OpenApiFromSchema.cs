// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.Standalone.Data;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;

namespace Cordango.Standalone.Http;

/// <summary>
/// Puts the application's own shapes into the OpenAPI document ASP.NET Core builds.
///
/// <para><b>Why a transformer and not reflection.</b> Every record route takes
/// <c>[FromBody] JsonElement</c> and returns <c>IActionResult</c>, because a partial update has to
/// know which keys the client actually named and a typed parameter cannot tell absent from null.
/// The framework describes exactly what it sees: every path, every verb, and a request body typed
/// <c>object</c>. That document is honest and no use to anyone.</para>
///
/// <para>So the ROUTES come from the framework — including any endpoint added by hand after
/// generation, which is why this is a transformer rather than a document written from scratch — and
/// the SHAPES come from <see cref="AppSchemaCatalogue"/>, which the generator emitted from the same
/// manifest that produced the controllers. Neither half is invented here.</para>
///
/// <para><b>The schemas arrive as JSON and stay JSON.</b> They were produced by the compiler's one
/// field-type map, so parsing them into the document's model is a change of representation. Setting
/// <see cref="OpenApiSchema"/> properties by hand instead would be a SECOND map from definition
/// types to schema keywords, and the two would disagree the first time a field type was added.</para>
/// </summary>
public sealed class OpenApiFromSchema : IOpenApiDocumentTransformer
{
    /// <summary>Matches <c>RecordsController.MaxPageSize</c>. Stated in the document because a
    /// client that asks for 5000 rows silently receives 500, and finding that out from a short page
    /// is worse than reading it.</summary>
    private const int MaxPageSize = 500;

    private readonly AppSchemaCatalogue _schema;

    public OpenApiFromSchema(AppSchemaCatalogue schema) => _schema = schema;

    public Task TransformAsync(
        OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        document.Info ??= new OpenApiInfo();
        document.Info.Title = $"{_schema.AppName} API";
        document.Info.Version = _schema.AppVersion;
        document.Info.Description = Overview();

        foreach (var entity in _schema.Entities) Describe(document, entity);

        return Task.CompletedTask;
    }

    /// <summary>What a reader needs before the first route makes sense: that the wire uses the
    /// definition's own field keys, and that a 403 here is about a role rather than a session.</summary>
    private string Overview() =>
        $$"""
          {{_schema.Description ?? _schema.AppName}}

          Every field is named by its key from the app definition, so `spent_on` rather than
          `spentOn`. Errors share one shape: `{ "code": "record.read_denied", "error": "..." }`,
          where `code` is stable and switchable and `error` is a sentence already translated for
          the caller.

          A 401 means you are not signed in. A 403 means you are, and your role does not allow this
          — the same roles the application's own screens obey, resolved per request. Fields your
          role may not read are ABSENT from a response rather than null, so do not read a missing
          key as an empty value.
          """;

    private void Describe(OpenApiDocument document, EntitySchema entity)
    {
        var read = entity.Key;
        var create = $"{entity.Key}_create";
        var update = $"{entity.Key}_update";

        Component(document, read, entity.ReadSchema);
        Component(document, create, entity.CreateSchema);
        Component(document, update, entity.UpdateSchema);

        var one = entity.Label.ToLowerInvariant();
        var many = entity.LabelPlural.ToLowerInvariant();
        var collection = $"/api/{entity.Key}";

        Operation(document, collection, HttpMethod.Get, op =>
        {
            op.Summary = $"List {many}";
            op.Parameters = ListParameters(entity);
            Response(op, "200", $"A page of {many}.", Schema(document, ListEnvelope(read)));
        });

        Operation(document, collection, HttpMethod.Post, op =>
        {
            op.Summary = $"Create a {one}";
            op.RequestBody = Body(create);
            Response(op, "201", "Created.", Ref(read));
        });

        Operation(document, $"{collection}/aggregate", HttpMethod.Get, op =>
        {
            op.Summary = $"Count or total {many}";
            op.Description =
                "Computed by the database rather than by reading every row. `groupBy` also accepts "
                + "`month_of:<field>`. A field your role may not read may not be aggregated either, "
                + "because an aggregate is a slower way of reading a field and not a different kind "
                + "of access.";
            op.Parameters = AggregateParameters();
            Response(op, "200", "One figure, or one per bucket.", Schema(document, AggregateEnvelope()));
        });

        Operation(document, $"{collection}/{{id}}", HttpMethod.Get, op =>
        {
            op.Summary = $"Get one {one}";
            Response(op, "200", entity.Label, Ref(read));
        });

        Operation(document, $"{collection}/{{id}}", HttpMethod.Put, op =>
        {
            op.Summary = $"Replace a {one}";
            op.Description = "Writes EVERY field. A key left out is cleared rather than kept — use "
                + "PATCH to change only some of them.";
            op.RequestBody = Body(create);
            Response(op, "200", entity.Label, Ref(read));
        });

        Operation(document, $"{collection}/{{id}}", HttpMethod.Patch, op =>
        {
            op.Summary = $"Change part of a {one}";
            op.Description = "Writes only the keys present in the body.";
            op.RequestBody = Body(update);
            Response(op, "200", entity.Label, Ref(read));
        });

        Operation(document, $"{collection}/{{id}}", HttpMethod.Delete, op =>
        {
            op.Summary = $"Delete a {one}";
            Response(op, "204", "Deleted.", null);
        });

        var commands = _schema.CommandsFor(entity.Key).ToList();
        if (commands.Count == 0) return;

        Operation(document, $"{collection}/{{id}}/commands/{{command}}", HttpMethod.Post, op =>
        {
            op.Summary = $"Run a command on a {one}";
            op.Description = CommandProse(commands);
            op.RequestBody = CommandBody(document, commands);
            Response(op, "200", "The record as it now stands, and a message to show.",
                Schema(document, CommandEnvelope(read)));
        });
    }

    /// <summary>
    /// The commands, as prose on the one route that runs them.
    ///
    /// <para>They share a path — <c>{command}</c> is a path segment — so there is nowhere in OpenAPI
    /// to hang one schema per command off. Listing them is what a reader can act on; the
    /// machine-readable form of the same list is the MCP tool schema.</para>
    /// </summary>
    private static string CommandProse(IReadOnlyList<CommandSchema> commands) =>
        "`command` is one of: "
        + string.Join(", ", commands.Select(c => $"`{c.Key}` ({c.Label})"))
        + ". A command is refused when your role does not allow it, and refused separately when the "
        + "record is not in a state it may run from — in that order, so being told 'not from this "
        + "state' never reveals a command you were not allowed to try.";

    // ---- the document model -------------------------------------------------------------------

    /// <summary>A JSON Schema, as the document's own type. <c>OpenApi3_1</c> because that revision
    /// IS JSON Schema — under 3.0 the reader would quietly drop keywords the compiler emitted.</summary>
    private static IOpenApiSchema Schema(OpenApiDocument document, string json) =>
        OpenApiModelFactory.Parse<OpenApiSchema>(json, OpenApiSpecVersion.OpenApi3_1, document, out _, "json")
        ?? new OpenApiSchema();

    private static IOpenApiSchema Ref(string component) => new OpenApiSchemaReference(component);

    private void Component(OpenApiDocument document, string name, string json)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal);
        document.Components.Schemas[name] = Schema(document, json);
    }

    private static string ListEnvelope(string item) =>
        $$"""
          {
            "type": "object",
            "properties": {
              "items": { "type": "array", "items": { "$ref": "#/components/schemas/{{item}}" } },
              "total": { "type": "integer", "description": "Rows matching the filter, counted before paging." },
              "skip": { "type": "integer" },
              "take": { "type": "integer" }
            }
          }
          """;

    private static string AggregateEnvelope() =>
        """
        {
          "type": "object",
          "properties": {
            "op": { "type": "string" },
            "buckets": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "key": { "type": "string" },
                  "value": { "type": "number" }
                }
              }
            }
          }
        }
        """;

    private static string CommandEnvelope(string item) =>
        $$"""
          {
            "type": "object",
            "properties": {
              "record": { "$ref": "#/components/schemas/{{item}}" },
              "message": { "type": "string" }
            }
          }
          """;

    private static OpenApiRequestBody Body(string component) => new()
    {
        Required = true,
        Content = new Dictionary<string, OpenApiMediaType>(StringComparer.Ordinal)
        {
            ["application/json"] = new() { Schema = Ref(component) },
        },
    };

    /// <summary>One body covering every command on the entity. <c>oneOf</c> rather than one merged
    /// object, because two commands may want the same key for different things.</summary>
    private OpenApiRequestBody CommandBody(OpenApiDocument document, IReadOnlyList<CommandSchema> commands)
    {
        var options = string.Join(",\n", commands.Select(c => Titled(c)));

        return new OpenApiRequestBody
        {
            Required = true,
            Content = new Dictionary<string, OpenApiMediaType>(StringComparer.Ordinal)
            {
                ["application/json"] = new()
                {
                    Schema = Schema(document, $"{{ \"oneOf\": [\n{options}\n] }}"),
                },
            },
        };

        static string Titled(CommandSchema command)
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(command.InputSchema)!;
            node["title"] = command.Key;
            node["description"] = command.Label;
            return node.ToJsonString();
        }
    }

    private static void Response(
        OpenApiOperation operation, string status, string description, IOpenApiSchema? schema)
    {
        operation.Responses ??= new OpenApiResponses();

        var response = new OpenApiResponse { Description = description };
        if (schema is not null)
            response.Content = new Dictionary<string, OpenApiMediaType>(StringComparer.Ordinal)
            {
                ["application/json"] = new() { Schema = schema },
            };

        operation.Responses[status] = response;
    }

    private static List<IOpenApiParameter> ListParameters(EntitySchema entity) =>
    [
        Query("filter",
            "Repeatable, `field:operator:value` — `?filter=status:eq:open&filter=amount:gt:100`. "
            + "Operators: eq, neq, gt, gte, lt, lte, in, notIn, contains, startsWith, isEmpty, "
            + "isNotEmpty. A field this entity does not have is refused by name rather than ignored.",
            """{ "type": "array", "items": { "type": "string" } }"""),
        Query("sort",
            $"Comma-separated field keys, a leading minus for descending — `-created_at,{entity.DisplayField ?? "id"}`. "
            + "The id is always the last term, so a page boundary never splits equal rows.",
            """{ "type": "string" }"""),
        Query("skip", "Rows to skip.", """{ "type": "integer", "default": 0, "minimum": 0 }"""),
        Query("take", $"Rows to return. Clamped to {MaxPageSize}.",
            $$"""{ "type": "integer", "default": 50, "minimum": 1, "maximum": {{MaxPageSize}} }"""),
    ];

    private static List<IOpenApiParameter> AggregateParameters() =>
    [
        Query("op", "One of count, sum, avg, min, max.",
            """{ "type": "string", "default": "count", "enum": ["count", "sum", "avg", "min", "max"] }"""),
        Query("field", "The field to total. Required by every op except count.", """{ "type": "string" }"""),
        Query("groupBy", "A field key, or `month_of:<field>` for a field holding a date.",
            """{ "type": "string" }"""),
        Query("filter", "As on the list route.",
            """{ "type": "array", "items": { "type": "string" } }"""),
    ];

    private static IOpenApiParameter Query(string name, string description, string schemaJson) =>
        new OpenApiParameter
        {
            Name = name,
            In = ParameterLocation.Query,
            Required = false,
            Description = description,
            Schema = Schema(new OpenApiDocument(), schemaJson),
        };

    /// <summary>
    /// Describe one operation the framework already found, or do nothing.
    ///
    /// <para>Doing nothing is the correct answer often enough to be the default: a generated
    /// application can be built without a route this class knows the name of — an entity whose
    /// controller somebody deleted, a definition the emitters skipped — and a document that omits
    /// what is not served is right. Adding the path instead would advertise a 404.</para>
    /// </summary>
    private static void Operation(
        OpenApiDocument document, string path, HttpMethod method, Action<OpenApiOperation> describe)
    {
        if (document.Paths?.TryGetValue(path, out var item) != true) return;
        if (item is not OpenApiPathItem concrete) return;
        if (concrete.Operations?.TryGetValue(method, out var operation) != true || operation is null) return;

        describe(operation);
    }
}

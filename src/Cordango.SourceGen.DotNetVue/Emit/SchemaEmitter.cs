// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Compile;
using Cordango.SourceGen.Common;

namespace Cordango.SourceGen.DotNetVue.Emit;

/// <summary>
/// The application, described in JSON Schema, compiled into its own source.
///
/// <para><b>Why this exists at all.</b> Every route a generated application serves takes
/// <c>JsonElement</c> in and returns <c>IActionResult</c> out, because a partial update has to know
/// which keys the client actually named and a typed parameter cannot tell absent from null. That is
/// the right shape for the controller and a useless one for a reader: reflection over it produces a
/// document listing every route with every body typed <c>object</c> — accurate, and no help to
/// anyone.</para>
///
/// <para>The types are in the manifest, and the manifest is here at build time, so the schemas are
/// worked out once and emitted. The OpenAPI document and the MCP server then read the same object,
/// which is the only way a REST client and an AI client can be guaranteed the same answer about the
/// same field.</para>
///
/// <para>The field-type map is <see cref="FieldJsonSchema"/> in the compiler, shared with the hosted
/// platform's own document rather than copied.</para>
/// </summary>
public static class SchemaEmitter
{
    /// <summary>Fields a caller never writes: the ones the runtime owns, the ones the definition
    /// marks read-only, and the ones an expression computes. All three would be silently discarded
    /// on the way in, and a schema that offers them is a schema that lies.</summary>
    private static bool IsWritable(FieldModel field) =>
        !field.IsBase && !field.System && !field.ReadOnly && field.Computed is null;

    public static GeneratedFile Emit(AppModel app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var source = new Source();
        source.Line("using Cordango.Standalone.Data;");
        source.Line();
        source.Line($"namespace {app.Namespace}.Data;");
        source.Line();
        source.Line("/// <summary>");
        source.Line($"/// What {app.Name} contains, as JSON Schema — the one description its OpenAPI document and its");
        source.Line("/// MCP server both answer from.");
        source.Line("///");
        source.Line("/// <para>Worked out by the compiler at build time from the same manifest that produced the");
        source.Line("/// entities beside it, so it cannot drift from them. Regenerating replaces this file.</para>");
        source.Line("/// </summary>");
        source.Open("public static class AppSchema");
        source.Line("public static readonly AppSchemaCatalogue Catalogue = new(");
        source.Indent();
        source.Line($"{Naming.Literal(app.Key)},");
        source.Line($"{Naming.Literal(app.Name)},");
        source.Line($"{Naming.Literal(app.Version)},");
        source.Line($"{Naming.Literal(app.Description)},");
        source.Line("[");
        source.Indent();

        foreach (var entity in app.Entities)
        {
            source.Line($"new EntitySchema({Naming.Literal(entity.Key)}, {Naming.Literal(entity.Label)},");
            source.Indent();
            source.Line($"{Naming.Literal(entity.LabelPlural)}, {Naming.Literal(entity.DisplayField)},");
            source.Line($"ReadSchema: {Naming.Literal(ReadSchema(entity))},");
            source.Line($"CreateSchema: {Naming.Literal(WriteSchema(entity, mandatory: true))},");
            source.Line($"UpdateSchema: {Naming.Literal(WriteSchema(entity, mandatory: false))}),");
            source.Outdent();
        }

        source.Outdent();
        source.Line("],");
        source.Line("[");
        source.Indent();

        foreach (var command in app.Commands)
        {
            source.Line($"new CommandSchema({Naming.Literal(command.Key)}, {Naming.Literal(command.Label)},");
            source.Indent();
            source.Line($"{Naming.Literal(command.Entity)}, {Naming.Literal(InputSchema(app, command))}),");
            source.Outdent();
        }

        source.Outdent();
        source.Line("]);");
        source.Outdent();
        source.Close();

        return new GeneratedFile("api/Data/AppSchema.cs", source.ToString());
    }

    /// <summary>Everything a reader sees, base columns included — <c>id</c> and the audit stamps are
    /// on every record that comes back, so a document that omitted them would describe a shape the
    /// application never sends.</summary>
    private static string ReadSchema(EntityModel entity) =>
        Json(FieldJsonSchema.ForObject(entity.Fields.Select(f => f.Json), required: null, describe: true));

    /// <summary>
    /// What a write accepts.
    ///
    /// <para><paramref name="mandatory"/> is the whole difference between create and patch: the same
    /// fields either way, but a patch means exactly the keys it names, so requiring one there would
    /// refuse the ordinary case of changing a single value.</para>
    /// </summary>
    private static string WriteSchema(EntityModel entity, bool mandatory)
    {
        var fields = entity.Fields.Where(IsWritable).ToList();

        // hideOnCreate is a form instruction rather than a permission — the field is writable, it is
        // simply not asked for up front — so it stays in the schema and only loses its requirement.
        var required = mandatory
            ? fields.Where(f => f.Required && !f.HideOnCreate).Select(f => f.Key).ToHashSet(StringComparer.Ordinal)
            : null;

        return Json(FieldJsonSchema.ForObject(fields.Select(f => f.Json), required, describe: true));
    }

    /// <summary>
    /// What running a command needs.
    ///
    /// <para>A command's input is a list of the entity's own field keys, so the schema is that
    /// entity's fields narrowed to the named ones. An unknown key is dropped rather than described:
    /// the emitter cannot say what shape it has, and inventing <c>string</c> would be a guess a
    /// client would then rely on.</para>
    /// </summary>
    private static string InputSchema(AppModel app, CommandModel command)
    {
        var entity = app.Entity(command.Entity);

        var fields = command.InputFields
            .Select(key => entity?.Field(key))
            .Where(f => f is not null)
            .Select(f => f!.Json);

        var required = command.RequiredInputFields.ToHashSet(StringComparer.Ordinal);

        return Json(FieldJsonSchema.ForObject(fields, required, describe: true));
    }

    /// <summary>Compact and in insertion order — the schema is a literal in a generated file, and two
    /// builds of one definition have to produce the same bytes.</summary>
    private static string Json(JsonNode node) => node.ToJsonString();
}

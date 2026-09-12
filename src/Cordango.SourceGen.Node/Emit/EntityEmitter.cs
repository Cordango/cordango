// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.SourceGen.Common;

namespace Cordango.SourceGen.Node.Emit;

/// <summary>
/// The application's data model, as TypeScript: one interface per entity and one descriptor beside
/// it.
///
/// <para><b>Both, and they are not the same thing.</b> The INTERFACE is for whoever writes code
/// against this application — it is checked at build time and disappears at run time. The
/// DESCRIPTOR is what the runtime reads: which fields exist and what type each one holds, used by
/// the store to build the table, by the query layer to compare a filter's value against the right
/// kind of thing, and by the wire layer to refuse a payload that says a date where a number
/// belongs.</para>
///
/// <para>Only the application's OWN fields are listed. The id and the audit columns are
/// deliberately absent from the descriptor: a client that could name <c>created_by</c> in a payload
/// could claim somebody else wrote the row. They appear on the interface, because reading them is
/// ordinary and only writing them is not.</para>
/// </summary>
public static class EntityEmitter
{
    public static GeneratedFile Emit(AppModel app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var source = new TsSource();

        source.Lines(Header.For(
            $"{app.Name}'s entities.",
            "One interface per entity, and the descriptor the runtime reads beside it. The interface is\n"
            + "checked at build time and gone at run time; the descriptor is what builds the table, types a\n"
            + "filter's value and refuses a payload that names a field this entity does not have.\n"
            + "\n"
            + "A field keeps its key. There is no mapping layer between what the definition called a field,\n"
            + "what the column is called and what you type — they are one string, all the way down."));

        source.Line();
        source.Line("import {");
        source.Line("  Dec,");
        source.Line("  Instant,");
        source.Line("  PlainDate,");
        source.Line("  RecordDescriptor,");
        source.Line("  type RecordRow,");
        source.Line("} from \"@cordango/standalone\";");
        source.Line();

        // Imported for its side effect on the reader rather than on the compiler: an application
        // with no decimal, date or datetime field anywhere would not otherwise use these, and an
        // unused import is an error under `noUnusedLocals`. Referencing them in one exported tuple
        // keeps the import honest without a conditional emitter.
        source.Lines(TypeScript.Doc(
            "The value types a record can hold, re-exported so that code written against this "
            + "application does not have to know which package they came from.", 0));
        source.Line("export { Dec, Instant, PlainDate };");

        foreach (var entity in app.Entities)
        {
            source.Line();
            EmitInterface(source, entity);
            source.Line();
            EmitDescriptor(source, entity);
        }

        return new GeneratedFile("api/src/entities.ts", source.ToString());
    }

    private static void EmitInterface(TsSource source, EntityModel entity)
    {
        var doc = entity.Label;
        var described = AppModel.Str(entity.Json["description"]);
        if (described is { Length: > 0 }) doc += ".\n\n" + described;

        source.Lines(TypeScript.Doc(doc, 0));
        source.Open($"export interface {entity.TypeName} extends RecordRow");

        foreach (var field in entity.AuthoredFields)
        {
            if (field.Help is { Length: > 0 } help) source.Lines(TypeScript.Doc(help, 0));
            source.Line($"{TypeScript.Key(field.Key)}: {TypeScript.FieldType(field)};");
        }

        if (entity.HasTracking)
        {
            source.Line();
            source.Line("/** Stamped by the runtime. Readable by anyone who may read the record, and");
            source.Line(" *  writable by nothing: a payload that could name these could claim somebody");
            source.Line(" *  else wrote the row. */");
            source.Line("created_at?: Instant | null;");
            source.Line("created_by?: string | null;");
            source.Line("updated_at?: Instant | null;");
            source.Line("updated_by?: string | null;");
        }

        source.Close();
    }

    private static void EmitDescriptor(TsSource source, EntityModel entity)
    {
        var name = TypeScript.Identifier(entity.Key) + "Descriptor";

        source.Lines(TypeScript.Doc(
            $"What the runtime knows about {entity.Label}: its key, its label, and the type of every "
            + "field it carries.", 0));

        source.OpenCall($"export const {name} = new RecordDescriptor<{entity.TypeName}>");
        source.Line($"{TypeScript.Literal(entity.Key)},");
        source.Line($"{TypeScript.Literal(entity.Label)},");
        source.OpenArray();

        foreach (var field in entity.AuthoredFields)
            source.Line(
                $"{{ key: {TypeScript.Literal(field.Key)}, "
                + $"type: {TypeScript.Literal(TypeScript.DescriptorType(field.Type))} }},");

        source.CloseArray(",");
        source.CloseCall();
    }
}

/// <summary>The banner every generated TypeScript file carries. Says what wrote the file and what
/// happens to it on the next build, because a file that looks hand-written and is overwritten
/// nightly is the worst kind of surprise to inherit.</summary>
public static class Header
{
    public static string For(string title, string? detail = null)
    {
        var source = new TsSource();
        source.Line("// " + title);
        source.Line("//");

        if (detail is { Length: > 0 })
        {
            foreach (var line in detail.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
                source.Line(line.Length == 0 ? "//" : "// " + line);
            source.Line("//");
        }

        source.Line("// Generated by Cordango from this application's App Definition. Regenerating replaces this");
        source.Line("// file; anything you add of your own belongs in a file beside it, and cordango.build.json");
        source.Line("// lists exactly which files a build owns.");
        return source.ToString().TrimEnd('\n');
    }
}

// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.SourceGen.Common;

namespace Cordango.SourceGen.DotNetVue.Emit;

/// <summary>
/// The first EF migration, and the model snapshot that lets the user write the second one.
///
/// <para><b>The migration is a file in the user's repository, not a diff computed at startup.</b>
/// The prior art this design was assessed against runs a model-versus-database comparison on every
/// boot, with data loss permitted and the resulting exception written to the console. A migration
/// you can read, review in a pull request and roll back is the whole reason to be on EF at all.</para>
///
/// <para><b>The id is pinned.</b> <c>00000000000001_InitialCreate</c> rather than a timestamp,
/// because a timestamp would make two builds of the same definition produce different applications —
/// and the second build would then look like a schema change to anyone comparing them. Every
/// migration the user adds afterwards gets a real timestamp from EF, which sorts after this one.</para>
///
/// <para><b>The snapshot is what makes the next migration possible.</b> EF diffs a new model against
/// the snapshot, so a snapshot that does not exactly describe this schema makes the user's first
/// <c>dotnet ef migrations add</c> emit changes nobody asked for. That the two agree is not
/// established by care: <c>MigrationFidelityTests</c> generates an application, builds it, runs
/// <c>dotnet ef migrations add</c> and asserts the result is EMPTY.</para>
/// </summary>
public static class MigrationEmitter
{
    public const string MigrationId = "00000000000001_InitialCreate";
    private const string MigrationName = "InitialCreate";

    public static IEnumerable<GeneratedFile> Emit(AppModel app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var tables = SchemaModel.Build(app);

        yield return Migration(app, tables);
        yield return Designer(app, tables);
        yield return Snapshot(app, tables);
    }

    private static GeneratedFile Migration(AppModel app, IReadOnlyList<TableSchema> tables)
    {
        var source = new Source();
        source.Line("using System.Collections.Generic;");
        source.Line("using System.Text.Json;");
        source.Line("using Microsoft.EntityFrameworkCore.Migrations;");
        source.Line();
        source.Line("#nullable disable");
        source.Line();
        source.Line($"namespace {app.Namespace}.Migrations;");
        source.Line();
        source.Line($"/// <summary>Everything {app.Name} stores, created from nothing.</summary>");
        source.Open("public partial class InitialCreate : Migration");

        source.Open("protected override void Up(MigrationBuilder migrationBuilder)");
        var first = true;
        foreach (var table in tables)
        {
            if (!first) source.Line();
            first = false;
            CreateTable(source, table);
        }

        foreach (var table in tables)
            foreach (var index in table.Indexes)
            {
                source.Line();
                CreateIndex(source, table, index);
            }

        source.Close();
        source.Line();

        source.Open("protected override void Down(MigrationBuilder migrationBuilder)");
        // Dropped in reverse, which costs nothing here and is the habit that matters once a
        // migration has foreign keys in it.
        foreach (var table in tables.Reverse())
            source.Line($"migrationBuilder.DropTable(name: {Naming.Literal(table.Table)});");
        source.Close();

        source.Close();
        return new GeneratedFile($"api/Migrations/{MigrationId}.cs", source.ToString());
    }

    private static void CreateTable(Source source, TableSchema table)
    {
        source.Line("migrationBuilder.CreateTable(");
        source.Indent();
        source.Line($"name: {Naming.Literal(table.Table)},");
        source.Line("columns: table => new");
        source.Line("{");
        source.Indent();

        foreach (var column in table.Columns)
        {
            var parts = new List<string> { $"type: {Naming.Literal(column.StoreType)}" };
            if (column.MaxLength is { } max) parts.Insert(0, $"maxLength: {max}");
            parts.Add($"nullable: {(column.Nullable ? "true" : "false")}");

            // maxLength comes before type in EF's own output; matching the order keeps a diff
            // against a re-scaffolded migration readable.
            var ordered = column.MaxLength is null
                ? $"type: {Naming.Literal(column.StoreType)}, nullable: {(column.Nullable ? "true" : "false")}"
                : $"type: {Naming.Literal(column.StoreType)}, maxLength: {column.MaxLength}, nullable: {(column.Nullable ? "true" : "false")}";

            source.Line($"{column.Column} = table.Column<{column.ClrType}>({ordered}),");
        }

        source.Outdent();
        source.Line("},");
        source.Line("constraints: table =>");
        source.Line("{");
        source.Indent();

        var key = table.Key.Count == 1
            ? $"x => x.{ColumnFor(table, table.Key[0])}"
            : "x => new { " + string.Join(", ", table.Key.Select(k => "x." + ColumnFor(table, k))) + " }";

        source.Line($"table.PrimaryKey({Naming.Literal("PK_" + table.Table)}, {key});");
        source.Outdent();
        source.Line("});");
        source.Outdent();
    }

    private static void CreateIndex(Source source, TableSchema table, IndexSchema index)
    {
        source.Line("migrationBuilder.CreateIndex(");
        source.Indent();
        source.Line($"name: {Naming.Literal(index.Name(table.Table))},");
        source.Line($"table: {Naming.Literal(table.Table)},");

        source.Line(index.Columns.Count == 1
            ? $"column: {Naming.Literal(index.Columns[0])}{(index.Unique ? "," : ");")}"
            : $"columns: new[] {{ {string.Join(", ", index.Columns.Select(Naming.Literal))} }}{(index.Unique ? "," : ");")}");

        if (index.Unique) source.Line("unique: true);");
        source.Outdent();
    }

    /// <summary>
    /// The CLR type as a model snapshot spells it.
    ///
    /// <para>A nullable value type is written <c>DateTimeOffset?</c> here and plain
    /// <c>DateTimeOffset</c> in the migration, which looks like an inconsistency and is not: the
    /// migration describes a COLUMN, whose nullability is a separate argument, while the snapshot
    /// describes the MODEL, where the CLR type carries it. Getting this wrong does not fail —
    /// it makes the user's next `dotnet ef migrations add` full of AlterColumn calls that change
    /// nothing.</para>
    /// </summary>
    private static string Snapshot(ColumnSchema column) =>
        column.Nullable && IsValueType(column.ClrType) ? column.ClrType + "?" : column.ClrType;

    private static bool IsValueType(string clrType) =>
        clrType is "long" or "int" or "decimal" or "double" or "bool" or "DateOnly" or "DateTimeOffset" or "Guid";

    private static string ColumnFor(TableSchema table, string property) =>
        table.Columns.First(c => c.Property == property).Column;

    /// <summary>The migration's own attributes and the model it produces. EF splits these into a
    /// <c>.Designer.cs</c>; the split is a convention, and following it means a re-scaffold lands in
    /// the file a reader expects.</summary>
    private static GeneratedFile Designer(AppModel app, IReadOnlyList<TableSchema> tables)
    {
        var source = new Source();
        Model(source, app, tables,
            header:
            [
                // Unconditionally, the way `dotnet ef` writes them itself: the model may mention
                // List<string> for a multiselect and JsonDocument for a json field, and a snapshot
                // whose usings depend on which field types an application happens to have is one
                // that compiles for five applications and not the sixth. helpdesk was the sixth.
                "using System.Collections.Generic;",
                "using System.Text.Json;",
                "using Microsoft.EntityFrameworkCore;",
                "using Microsoft.EntityFrameworkCore.Infrastructure;",
                "using Microsoft.EntityFrameworkCore.Migrations;",
                "using Microsoft.EntityFrameworkCore.Storage.ValueConversion;",
                $"using {app.Namespace}.Data;",
                "",
                "#nullable disable",
                "",
                $"namespace {app.Namespace}.Migrations;",
                "",
                "[DbContext(typeof(AppDbContext))]",
                $"[Migration({Naming.Literal(MigrationId)})]",
                "partial class " + MigrationName,
            ],
            method: "protected override void BuildTargetModel(ModelBuilder modelBuilder)");

        return new GeneratedFile($"api/Migrations/{MigrationId}.Designer.cs", source.ToString());
    }

    /// <summary>The model as it stands after every migration — what EF diffs the next one against.</summary>
    private static GeneratedFile Snapshot(AppModel app, IReadOnlyList<TableSchema> tables)
    {
        var source = new Source();
        Model(source, app, tables,
            header:
            [
                // Unconditionally, the way `dotnet ef` writes them itself: the model may mention
                // List<string> for a multiselect and JsonDocument for a json field, and a snapshot
                // whose usings depend on which field types an application happens to have is one
                // that compiles for five applications and not the sixth. helpdesk was the sixth.
                "using System.Collections.Generic;",
                "using System.Text.Json;",
                "using Microsoft.EntityFrameworkCore;",
                "using Microsoft.EntityFrameworkCore.Infrastructure;",
                "using Microsoft.EntityFrameworkCore.Storage.ValueConversion;",
                $"using {app.Namespace}.Data;",
                "",
                "#nullable disable",
                "",
                $"namespace {app.Namespace}.Migrations;",
                "",
                "[DbContext(typeof(AppDbContext))]",
                "partial class AppDbContextModelSnapshot : ModelSnapshot",
            ],
            method: "protected override void BuildModel(ModelBuilder modelBuilder)");

        return new GeneratedFile("api/Migrations/AppDbContextModelSnapshot.cs", source.ToString());
    }

    private static void Model(Source source, AppModel app, IReadOnlyList<TableSchema> tables, string[] header, string method)
    {
        foreach (var line in header[..^1]) source.Line(line);
        source.Open(header[^1]);
        source.Open(method);

        source.Line("#pragma warning disable 612, 618");
        source.Line("modelBuilder.HasAnnotation(\"Relational:MaxIdentifierLength\", 63);");
        source.Line();
        source.Line("NpgsqlModelBuilderExtensions.UseIdentityByDefaultColumns(modelBuilder);");

        foreach (var table in tables)
        {
            source.Line();
            source.Open($"modelBuilder.Entity({Naming.Literal(table.ClrType)}, b =>");

            foreach (var column in table.Columns)
            {
                source.Line($"b.Property<{Snapshot(column)}>({Naming.Literal(column.Property)})");
                source.Indent();
                if (!column.Nullable) source.Line(".IsRequired()");
                if (column.MaxLength is { } max) source.Line($".HasMaxLength({max})");
                source.Line($".HasColumnType({Naming.Literal(column.StoreType)})");
                source.Line($".HasColumnName({Naming.Literal(column.Column)});");
                source.Outdent();
                source.Line();
            }

            source.Line($"b.HasKey({string.Join(", ", table.Key.Select(Naming.Literal))});");

            foreach (var index in table.Indexes)
            {
                source.Line();
                var properties = string.Join(", ", index.Properties.Select(Naming.Literal));
                source.Line($"b.HasIndex({properties}){(index.Unique ? ".IsUnique()" : "")};");
            }

            source.Line();
            source.Line($"b.ToTable({Naming.Literal(table.Table)}, (string)null);");
            source.Close(");");
        }

        source.Line("#pragma warning restore 612, 618");
        source.Close();
        source.Close();
    }
}

// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.SourceGen.Common;

namespace Cordango.SourceGen.DotNetVue.Emit;

/// <summary>
/// One <see cref="Microsoft.EntityFrameworkCore.IEntityTypeConfiguration{TEntity}"/> per entity:
/// <c>api/Data/&lt;Entity&gt;Configuration.cs</c>.
///
/// <para>EF's own extension point, which is the reason this shape was worth keeping from the prior
/// art: the mapping for one entity is one file, in the framework's vocabulary, and a person who
/// wants to add an index or change a column type edits the thing EF already documents rather than
/// something we invented.</para>
///
/// <para><b>Column names are the definition's field keys.</b> A query log, a database console and
/// the definition then say the same word for the same thing, which is what makes a production
/// problem traceable back to the application it came from.</para>
/// </summary>
public static class ConfigurationEmitter
{
    public static GeneratedFile Emit(AppModel app, EntityModel entity)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(entity);

        var source = new Source();
        source.Line("using Microsoft.EntityFrameworkCore;");
        source.Line("using Microsoft.EntityFrameworkCore.Metadata.Builders;");
        source.Line($"using {app.Namespace}.Entities;");
        source.Line();
        source.Line($"namespace {app.Namespace}.Data;");
        source.Line();
        source.Line($"/// <summary>How {entity.Label} rows map to the <c>{entity.Table}</c> table.</summary>");
        source.Open($"public sealed class {entity.TypeName}Configuration : IEntityTypeConfiguration<{entity.TypeName}>");
        source.Open($"public void Configure(EntityTypeBuilder<{entity.TypeName}> builder)");

        source.Line($"builder.ToTable({Naming.Literal(entity.Table)});");
        source.Line("builder.HasKey(e => e.Id);");
        source.Line();

        foreach (var field in entity.Fields)
        {
            var property = PropertyOf(entity, field);
            var calls = new List<string> { $"HasColumnName({Naming.Literal(field.Column)})" };

            if (ColumnType(field) is { } columnType) calls.Add($"HasColumnType({Naming.Literal(columnType)})");
            if (MaxLength(field) is { } max) calls.Add($"HasMaxLength({max})");
            if (field.Required && !field.IsCollection) calls.Add("IsRequired()");

            source.Line($"builder.Property(e => e.{property}).{string.Join(".", calls)};");
        }

        var indexes = Indexes(entity).ToList();
        if (indexes.Count > 0)
        {
            source.Line();
            foreach (var index in indexes) source.Line(index);
        }

        source.Close();
        source.Close();
        return new GeneratedFile($"api/Data/{entity.TypeName}Configuration.cs", source.ToString());
    }

    private static string PropertyOf(EntityModel entity, FieldModel field) => field.Key switch
    {
        "created_at" => "Created",
        "created_by" => "CreatedBy",
        "updated_at" => "LastModified",
        "updated_by" => "LastModifiedBy",
        _ => field.PropertyName,
    };

    /// <summary>
    /// The Postgres type, where the default mapping is not the one we want.
    ///
    /// <para>Left to the provider everywhere else: Npgsql already maps <c>DateOnly</c> to
    /// <c>date</c>, <c>decimal</c> to <c>numeric</c> and <c>List&lt;string&gt;</c> to <c>text[]</c>,
    /// and restating that here would be a second mapping table to keep in step with the first.</para>
    /// </summary>
    private static string? ColumnType(FieldModel field) => field.Type switch
    {
        // Explicit, because the provider would otherwise choose `json` for a JsonDocument and the
        // difference matters: only jsonb can be indexed, and only jsonb compares by value.
        "json" => "jsonb",
        // A money amount is not a float. numeric(18,4) holds every currency's minor unit exactly and
        // survives the sums a report does over it; the default numeric with no precision is
        // unbounded, which Postgres allows and every client library rounds differently.
        "money" => "numeric(18,4)",
        _ => null,
    };

    /// <summary>
    /// A length limit where one is meaningful.
    ///
    /// <para>Ids and references are bounded because they are keys. Free text is not: a definition
    /// that says <c>text</c> means "a line of text", and inventing a 200-character ceiling for it
    /// would reject real data — a customer name, an address, a subject line — for a constraint
    /// nobody asked for. Postgres stores unbounded text as efficiently as a capped varchar, so the
    /// cap buys nothing but rejections.</para>
    /// </summary>
    private static int? MaxLength(FieldModel field) => field.Type switch
    {
        "reference" => 64,
        _ => field.Key == "id" ? 64 : null,
    };

    private static IEnumerable<string> Indexes(EntityModel entity)
    {
        foreach (var field in entity.Fields)
        {
            var property = PropertyOf(entity, field);
            if (field.Key == "id") continue;

            if (field.Unique)
            {
                // Filtered, because "unique unless absent" is what an optional unique field means:
                // two records with no invoice number are not the same invoice.
                var filter = field.Required ? "" : ".HasFilter(null)";
                yield return $"builder.HasIndex(e => e.{property}).IsUnique(){filter};";
            }
            else if (field.Indexed || field.IsReference || field.Role == "status")
            {
                // References and the status field are what every list filters and groups by, so they
                // are indexed whether or not the author remembered to say so.
                yield return $"builder.HasIndex(e => e.{property});";
            }
        }
    }
}

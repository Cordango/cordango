// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.SourceGen.DotNetVue.Model;

namespace Cordango.SourceGen.DotNetVue.Emit;

/// <summary>One column, described the way both the migration and the model snapshot need it.</summary>
/// <param name="Column">The database column name.</param>
/// <param name="Property">The CLR property name EF knows it by.</param>
/// <param name="ClrType">The CLR type name, unadorned.</param>
/// <param name="StoreType">The Postgres type, spelled as Npgsql spells it.</param>
/// <param name="Nullable">Whether the column accepts null.</param>
/// <param name="MaxLength">Set only for bounded strings.</param>
public sealed record ColumnSchema(
    string Column,
    string Property,
    string ClrType,
    string StoreType,
    bool Nullable,
    int? MaxLength = null);

/// <summary>One index. Properties are what the model snapshot names; columns are what the
/// migration names, and EF's own index-name convention is built from the columns.</summary>
public sealed record IndexSchema(
    IReadOnlyList<string> Properties,
    IReadOnlyList<string> Columns,
    bool Unique)
{
    /// <summary>EF's convention: <c>IX_&lt;table&gt;_&lt;column&gt;…</c>.</summary>
    public string Name(string table) => "IX_" + table + "_" + string.Join("_", Columns);
}

/// <summary>One table, as the migration will create it and the snapshot will describe it.</summary>
public sealed record TableSchema(
    string ClrType,
    string Table,
    IReadOnlyList<ColumnSchema> Columns,
    IReadOnlyList<string> Key,
    IReadOnlyList<IndexSchema> Indexes);

/// <summary>
/// The database, described once, from the same place the migration and the snapshot are both
/// written.
///
/// <para><b>Why this exists rather than an EF model.</b> EF could describe the schema perfectly, and
/// it needs the compiled entity classes to do it — which do not exist while the generator is
/// deciding what those classes will say. So the shape is derived from the manifest here and the two
/// EF-facing files are written from it, which keeps them in step with each other by construction
/// rather than by care.</para>
///
/// <para>They still have to agree with what EF would infer from the emitted classes, and no amount
/// of care establishes that. <c>MigrationFidelityTests</c> does: it generates an application, builds
/// it, runs <c>dotnet ef migrations add</c> against it and asserts the new migration is EMPTY. An
/// empty diff is EF saying the snapshot already describes its model exactly.</para>
/// </summary>
public static class SchemaModel
{
    public static IReadOnlyList<TableSchema> Build(AppModel app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var tables = new List<TableSchema>();

        foreach (var entity in app.Entities)
            tables.Add(ForEntity(app, entity));

        tables.AddRange(Directory);
        tables.Add(Preferences);
        tables.Add(NotificationTable);

        // Ordinal by CLR type, which is the order EF writes a snapshot in. Matching it means the
        // fidelity test compares like with like instead of reporting a reordering as a difference.
        return [.. tables.OrderBy(t => t.ClrType, StringComparer.Ordinal)];
    }

    private static TableSchema ForEntity(AppModel app, EntityModel entity)
    {
        var clrType = $"{app.Namespace}.Entities.{entity.TypeName}";
        var columns = new List<ColumnSchema>();
        var indexes = new List<IndexSchema>();

        foreach (var field in entity.Fields)
        {
            var property = PropertyOf(field);
            var (clr, store, max) = Types(field);
            var nullable = !(field.Key == "id" || field.Required || field.IsCollection);

            // The tracking columns are typed by the interface, not by the definition. They agree in
            // practice — the compiler declares them as datetime — but the interface is what EF sees.
            if (field.Key is "created_at") { clr = "DateTimeOffset"; store = "timestamp with time zone"; nullable = false; max = null; }
            if (field.Key is "updated_at") { clr = "DateTimeOffset"; store = "timestamp with time zone"; nullable = true; max = null; }
            if (field.Key is "created_by" or "updated_by") { clr = "string"; store = "text"; nullable = true; max = null; }

            columns.Add(new ColumnSchema(field.Column, property, clr, store, nullable, max));

            if (field.Key == "id") continue;

            if (field.Unique)
                indexes.Add(new IndexSchema([property], [field.Column], Unique: true));
            else if (field.Indexed || field.IsReference || field.Role == "status")
                indexes.Add(new IndexSchema([property], [field.Column], Unique: false));
        }

        return new TableSchema(clrType, entity.Table, columns, ["Id"], indexes);
    }

    private static string PropertyOf(FieldModel field) => field.Key switch
    {
        "created_at" => "Created",
        "created_by" => "CreatedBy",
        "updated_at" => "LastModified",
        "updated_by" => "LastModifiedBy",
        _ => field.PropertyName,
    };

    /// <summary>CLR type, Postgres type, and the length limit if there is one — the one place the
    /// type mapping is written down.</summary>
    private static (string Clr, string Store, int? Max) Types(FieldModel field)
    {
        if (field.Key == "id" || field.Type == "reference")
            return ("string", "character varying(64)", 64);

        return field.Type switch
        {
            "integer" => ("long", "bigint", null),
            "decimal" => ("decimal", "numeric", null),
            "money" => ("decimal", "numeric(18,4)", null),
            "boolean" => ("bool", "boolean", null),
            "date" => ("DateOnly", "date", null),
            "datetime" => ("DateTimeOffset", "timestamp with time zone", null),
            "multiselect" => ("List<string>", "text[]", null),
            "json" => ("JsonDocument", "jsonb", null),
            _ => ("string", "text", null),
        };
    }

    /// <summary>Every application's directory. Described here rather than read from
    /// <c>Cordango.Standalone</c> because this project deliberately does not reference it — see the
    /// generator's project file. The fidelity test is what keeps the two honest.</summary>
    private static IEnumerable<TableSchema> Directory =>
    [
        new("Cordango.Standalone.Directory.Contact", "directory_contact",
        [
            Key("id"), Text("full_name", "FullName", nullable: false), Ref("organization", "OrganizationId"),
            Text("job_title", "JobTitle"), Text("email", "Email"), Text("phone", "Phone"),
            Text("mobile", "Mobile"), Flag("is_primary", "IsPrimary"), Text("status", "Status", nullable: false),
            Ref("owner", "Owner"), Text("notes", "Notes"), .. Tracking,
        ], ["Id"],
        [new IndexSchema(["OrganizationId"], ["organization"], false)]),

        new("Cordango.Standalone.Directory.Department", "directory_department",
        [
            Key("id"), Text("name", "Name", nullable: false), Text("handle", "Handle"),
            Ref("parent", "Parent"), Ref("lead", "Lead"), .. Tracking,
        ], ["Id"],
        [new IndexSchema(["Name"], ["name"], true)]),

        new("Cordango.Standalone.Directory.Group", "directory_group",
        [
            Key("id"), Text("name", "Name", nullable: false), Text("handle", "Handle"),
            Ref("parent", "Parent"), Text("description", "Description"), Text("group_type", "GroupType"),
            .. Tracking,
        ], ["Id"],
        [new IndexSchema(["Name"], ["name"], true)]),

        new("Cordango.Standalone.Directory.Organization", "directory_organization",
        [
            Key("id"), Text("name", "Name", nullable: false),
            new ColumnSchema("roles", "Roles", "List<string>", "text[]", false),
            Text("status", "Status", nullable: false), Text("industry", "Industry"),
            Text("website", "Website"), Text("email", "Email"), Text("phone", "Phone"),
            Text("street", "Street"), Text("postcode", "Postcode"), Text("city", "City"),
            Text("country", "Country"), Ref("owner", "Owner"), Text("notes", "Notes"), .. Tracking,
        ], ["Id"],
        [new IndexSchema(["Name"], ["name"], false)]),

        new("Cordango.Standalone.Directory.Person", "directory_person",
        [
            Key("id"), Text("full_name", "FullName", nullable: false), Text("email", "Email"),
            Ref("department", "Department"), Ref("manager", "Manager"), Text("location", "Location"),
            new ColumnSchema("hire_date", "HireDate", "DateOnly", "date", true),
            Text("employment_status", "EmploymentStatus", nullable: false),
            Flag("has_login", "HasLogin"), Ref("user_id", "UserId"), .. Tracking,
        ], ["Id"],
        [
            new IndexSchema(["Department"], ["department"], false),
            new IndexSchema(["Email"], ["email"], true),
            new IndexSchema(["FullName"], ["full_name"], false),
        ]),
    ];

    /// <summary>Each person's own table layouts. A composite key, so there is no id to tamper
    /// with.</summary>
    private static TableSchema Preferences => new(
        "Cordango.Standalone.Preferences.TableSetting", "user_table_settings",
        [
            new("user_id", "UserId", "string", "character varying(64)", false, 64),
            new("handle", "Handle", "string", "character varying(120)", false, 120),
            new("table_key", "TableKey", "string", "character varying(120)", false, 120),
            new("payload", "Payload", "string", "jsonb", false),
            new("updated", "Updated", "DateTimeOffset", "timestamp with time zone", false),
        ],
        ["UserId", "Handle", "TableKey"],
        []);

    /// <summary>What a command told somebody about.</summary>
    private static TableSchema NotificationTable => new(
        "Cordango.Standalone.Notifications.Notification", "notification",
        [
            Key("id"),
            new("person", "Person", "string", "character varying(64)", false, 64),
            new("title", "Title", "string", "text", false),
            new("message", "Message", "string", "text", true),
            new("link", "Link", "string", "text", true),
            new("created", "Created", "DateTimeOffset", "timestamp with time zone", false),
            new("read_at", "ReadAt", "DateTimeOffset", "timestamp with time zone", true),
        ],
        ["Id"],
        [new IndexSchema(["Person", "Created"], ["person", "created"], false)]);

    private static ColumnSchema Key(string column) =>
        new(column, "Id", "string", "character varying(64)", false, 64);

    private static ColumnSchema Ref(string column, string property) =>
        new(column, property, "string", "character varying(64)", true, 64);

    private static ColumnSchema Text(string column, string property, bool nullable = true) =>
        new(column, property, "string", "text", nullable);

    private static ColumnSchema Flag(string column, string property) =>
        new(column, property, "bool", "boolean", false);

    private static IEnumerable<ColumnSchema> Tracking =>
    [
        new("created", "Created", "DateTimeOffset", "timestamp with time zone", false),
        new("created_by", "CreatedBy", "string", "character varying(64)", true, 64),
        new("last_modified", "LastModified", "DateTimeOffset", "timestamp with time zone", true),
        new("last_modified_by", "LastModifiedBy", "string", "character varying(64)", true, 64),
    ];
}

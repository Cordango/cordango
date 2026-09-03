// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.SourceGen.Common;

namespace Cordango.SourceGen.DotNetVue.Emit;

/// <summary>
/// One plain class per entity: <c>api/Entities/&lt;Entity&gt;.cs</c>.
///
/// <para><b>Data and nothing else.</b> No behaviour, no navigation properties, no base class beyond
/// the two interfaces the runtime needs. That is what makes the file safe to overwrite on every
/// build, and it is why hooks live in the service container instead of here — a method on this class
/// would be deleted by the next generation, which is the one place a framework must not invite you
/// to put your logic.</para>
///
/// <para><b>References are ids, not objects.</b> A definition's reference field means "this field
/// holds that record's id", and it is stored as a string column. Giving the class a navigation
/// property would be more idiomatic EF and would make the generated model disagree with the language
/// it came from — a query would return an object where the definition, the API and the front end all
/// say string.</para>
///
/// <para><b>Written as a template rather than as a sequence of appends.</b> The emitted file is
/// visible here as the shape it will have: the braces balance by eye, the indentation is the
/// indentation, and there is no open/close bookkeeping to get wrong. What varies is spliced in
/// through <see cref="Code.Block"/>.</para>
/// </summary>
public static class EntityEmitter
{
    public static GeneratedFile Emit(AppModel app, EntityModel entity)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(entity);

        var interfaces = entity.HasTracking ? "IRecord, IHasTrackingFields" : "IRecord";

        // The identity, then one property per authored field. Built as a list and spliced as one
        // block so that an entity with no fields of its own does not leave a stray blank line.
        var members = new List<string>
        {
            """
            /// <summary>The record's identity. A string, because a Cordango id may be a
            /// generated key or a handle somebody typed.</summary>
            [JsonPropertyName("id")] public string Id { get; set; } = "";
            """,
        };

        members.AddRange(entity.Fields.Where(f => f.Key != "id").Select(Property));

        var source = $$"""
            using System.Text.Json;
            using System.Text.Json.Serialization;
            using Cordango.Standalone.Records;

            namespace {{app.Namespace}}.Entities;

            /// <summary>
            {{Doc.Summary(entity.Label, entity.Json["description"]?.GetValue<string>())}}
            /// </summary>
            public sealed class {{entity.TypeName}} : {{interfaces}}
            {
            {{Code.Block(members, indent: 4)}}
            }

            """;

        return new GeneratedFile($"api/Entities/{entity.TypeName}.cs", source);
    }

    /// <summary>One property, with whatever the definition can say about it above.</summary>
    private static string Property(FieldModel field)
    {
        // The four audit columns implement IHasTrackingFields, so their PROPERTY names are the
        // interface's and their JSON and column names stay the definition's. The runtime finds them
        // through the interface; everything a user sees still says created_at.
        var tracking = TrackingProperty(field.Key);
        var property = tracking ?? field.PropertyName;
        var declared = tracking is null ? field.DeclaredType : TrackingType(field.Key);
        var initialiser = tracking is null ? field.Initialiser : null;
        var suffix = initialiser is null ? "" : $" = {initialiser};";

        var declaration =
            $"[JsonPropertyName({Naming.Literal(field.Key)})] public {declared} {property} {{ get; set; }}{suffix}";

        return Doc.Field(field) is not { } comment
            ? declaration
            : $$"""
                /// <summary>
                {{comment}}
                /// </summary>
                {{declaration}}
                """;
    }

    /// <summary>The interface property an audit column is exposed as, or null for an ordinary
    /// field.</summary>
    private static string? TrackingProperty(string key) => key switch
    {
        "created_at" => "Created",
        "created_by" => "CreatedBy",
        "updated_at" => "LastModified",
        "updated_by" => "LastModifiedBy",
        _ => null,
    };

    /// <summary>The interface fixes these types, and the definition's <c>datetime</c> agrees with
    /// them — so this is a restatement, not a conversion.</summary>
    private static string TrackingType(string key) => key switch
    {
        "created_at" => "DateTimeOffset",
        "created_by" => "string?",
        "updated_at" => "DateTimeOffset?",
        "updated_by" => "string?",
        _ => "string?",
    };
}

/// <summary>Turning a definition's own words into doc comments, so a generated file explains itself
/// in the language of the business rather than in the language of the generator.</summary>
internal static class Doc
{
    public static string Summary(string label, string? description)
    {
        var text = string.IsNullOrWhiteSpace(description) ? label : $"{label}. {description.Trim()}";
        return Wrap(text);
    }

    public static string? Field(FieldModel field)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(field.Help)) parts.Add(field.Help.Trim());

        if (field.IsReference && field.TargetEntity is { } target)
            parts.Add($"Holds the id of a {target} record.");

        if (field.Type == "select" && field.Options.Count > 0)
        {
            var values = field.Options
                .Select(o => o["value"]?.GetValue<string>())
                .Where(v => v is not null)
                .Take(8);
            parts.Add("One of: " + string.Join(", ", values) + ".");
        }

        if (field.Type == "money" && field.Currency is { } currency) parts.Add($"In {currency}.");
        if (field.Unit is { } unit) parts.Add($"Displayed with the unit '{unit}'.");
        if (field.ReadOnly) parts.Add("Set by the runtime; not editable in a form.");

        if (parts.Count == 0) return null;
        return Wrap(field.Label + ". " + string.Join(" ", parts));
    }

    /// <summary>Wrap at a width that keeps a generated file readable next to hand-written code.</summary>
    private static string Wrap(string text)
    {
        const int width = 96;
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>();
        var current = "///";

        foreach (var word in words)
        {
            if (current.Length + 1 + word.Length > width)
            {
                lines.Add(current);
                current = "///";
            }
            current += " " + word;
        }

        if (current != "///") lines.Add(current);
        return string.Join('\n', lines);
    }
}

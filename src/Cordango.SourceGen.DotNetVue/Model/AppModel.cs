// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;

namespace Cordango.SourceGen.DotNetVue.Model;

/// <summary>
/// The compiled manifest, read once into shapes the emitters can use.
///
/// <para><b>Read from the MANIFEST, never from the definition.</b> The two look alike and are not
/// the same document: the manifest is what the compiler produced — base fields prepended, defaults
/// filled, options coloured, processes canonicalised, commands synthesized from transitions, views
/// and pages invented where the author wrote none. An emitter reading the definition would generate
/// an application missing everything the compiler decided, and would do it silently, because the
/// definition is also a valid document.</para>
///
/// <para>This class does no inference of its own. Anything not in the manifest is absent here too;
/// where a default is needed the emitter says so at the point of use, so there is one place per
/// question rather than a second compiler hiding in the generator.</para>
/// </summary>
public sealed class AppModel
{
    private AppModel(JsonObject manifest, string definitionHash)
    {
        Manifest = manifest;
        DefinitionHash = definitionHash;

        Key = Str(manifest["key"]) ?? "app";
        Name = Str(manifest["name"]) ?? Key;
        Version = Str(manifest["version"]) ?? "1.0.0";
        Description = Str(manifest["description"]);
        Namespace = Naming.Pascal(Key);

        Entities = [.. Arr(manifest["entities"]).OfType<JsonObject>().Select(e => new EntityModel(e, Namespace))];
        Views = [.. Arr(manifest["views"]).OfType<JsonObject>().Select(v => new ViewModel(v))];
        Pages = [.. Arr(manifest["pages"]).OfType<JsonObject>().Select(p => new PageModel(p))];
        Roles = [.. Arr(manifest["roles"]).OfType<JsonObject>()];
        Processes = [.. Arr(manifest["processes"]).OfType<JsonObject>().Select(p => new ProcessModel(p))];
        Commands = [.. Arr(manifest["commands"]).OfType<JsonObject>().Select(c => new CommandModel(c))];
        Workflows = [.. Arr(manifest["workflows"]).OfType<JsonObject>()];
        Theme = manifest["theme"] as JsonObject;
    }

    public static AppModel From(CompiledAppArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        return new AppModel(artifact.Manifest, artifact.DefinitionHash);
    }

    public JsonObject Manifest { get; }
    public string DefinitionHash { get; }

    public string Key { get; }
    public string Name { get; }
    public string Version { get; }
    public string? Description { get; }

    /// <summary>The C# namespace and assembly name.</summary>
    public string Namespace { get; }

    public IReadOnlyList<EntityModel> Entities { get; }
    public IReadOnlyList<ViewModel> Views { get; }
    public IReadOnlyList<PageModel> Pages { get; }
    public IReadOnlyList<JsonObject> Roles { get; }
    public IReadOnlyList<ProcessModel> Processes { get; }
    public IReadOnlyList<CommandModel> Commands { get; }
    public IReadOnlyList<JsonObject> Workflows { get; }
    public JsonObject? Theme { get; }

    public EntityModel? Entity(string? key) =>
        key is null ? null : Entities.FirstOrDefault(e => e.Key == key);

    public ViewModel? View(string? key) =>
        key is null ? null : Views.FirstOrDefault(v => v.Key == key);

    /// <summary>The process that governs an entity, if one does. At most one per entity — the
    /// compiler enforces that, so this does not have to decide between two.</summary>
    public ProcessModel? ProcessFor(string entityKey) =>
        Processes.FirstOrDefault(p => p.Entity == entityKey);

    public IEnumerable<CommandModel> CommandsFor(string entityKey) =>
        Commands.Where(c => c.Entity == entityKey);

    internal static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    internal static bool Bool(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    internal static JsonArray Arr(JsonNode? node) => node as JsonArray ?? [];
}

/// <summary>One entity: what it stores, what it is called, and how one record is laid out.</summary>
public sealed class EntityModel
{
    /// <param name="json">The entity, as the manifest gives it.</param>
    /// <param name="appNamespace">The application's root namespace. Needed here and not derivable
    /// here, because whether this entity's class name is available depends on what else is in scope
    /// in the files that will use it — see <see cref="Naming.Type"/>.</param>
    public EntityModel(JsonObject json, string? appNamespace = null)
    {
        Json = json;
        TypeName = Naming.Type(AppModel.Str(json["key"]) ?? "entity", appNamespace);
        Key = AppModel.Str(json["key"]) ?? "entity";
        Label = AppModel.Str(json["label"]) ?? Key;
        LabelPlural = AppModel.Str(json["labelPlural"]) ?? Label + "s";
        Icon = AppModel.Str(json["icon"]);
        DisplayField = AppModel.Str(json["displayField"]);
        Kind = AppModel.Str(json["kind"]);
        // company_id and app_id never reach the standalone model. The compiler adds them because a
        // platform row has to say which tenant and which application it belongs to; here the answer
        // is the same in every row of every table forever, and a column that always holds one value
        // is a column somebody will eventually filter on and be confused by.
        Fields =
        [
            .. AppModel.Arr(json["fields"]).OfType<JsonObject>()
                .Select(f => new FieldModel(f, Key))
                .Where(f => !FieldModel.DroppedKeys.Contains(f.Key)),
        ];
        Detail = json["detail"] as JsonObject;
        Peek = json["peek"] as JsonObject;
        Form = json["form"] as JsonObject;
    }

    public JsonObject Json { get; }
    public string Key { get; }
    public string Label { get; }
    public string LabelPlural { get; }
    public string? Icon { get; }
    public string? DisplayField { get; }

    /// <summary>A <c>settings</c> entity holds one row of configuration rather than a list of
    /// records. It still gets a table and a controller; only the screens differ.</summary>
    public string? Kind { get; }

    public IReadOnlyList<FieldModel> Fields { get; }
    public JsonObject? Detail { get; }
    public JsonObject? Peek { get; }
    public JsonObject? Form { get; }

    /// <summary>The C# class name for this entity's records. Usually <c>Pascal(key)</c>, and
    /// suffixed when that name is already taken by a namespace or a framework type.</summary>
    public string TypeName { get; }

    /// <summary>The key, Pascal-cased, with no collision handling — the front end has no C# scope to
    /// clash with, and a Vue component called <c>TaskRecordRecordPage</c> would be the C# problem
    /// leaking somewhere it does not exist.</summary>
    public string PascalKey => Naming.Pascal(Key);

    public string Table => Naming.Table(Key);

    public FieldModel? Field(string? key) => key is null ? null : Fields.FirstOrDefault(f => f.Key == key);

    /// <summary>Fields that are the application's own, as opposed to the ones every record has.
    /// These are what a form shows and what a descriptor copies.</summary>
    public IEnumerable<FieldModel> AuthoredFields => Fields.Where(f => !f.IsBase);

    /// <summary>True when this entity carries the audit columns, which in practice is always —
    /// the compiler prepends them. Asked rather than assumed so that an entity which somehow lacks
    /// them still generates something that compiles.</summary>
    public bool HasTracking =>
        Fields.Any(f => f.Key == "created_at") && Fields.Any(f => f.Key == "updated_at");
}

/// <summary>One field, with the two questions an emitter keeps asking: what C# type, and may it be
/// null.</summary>
public sealed class FieldModel
{
    /// <summary>Every record has these, and the runtime provides them. <c>company_id</c> and
    /// <c>app_id</c> are in the platform's list and NOT here: a standalone build is one application
    /// for one organisation, so a column naming which tenant and which app a row belongs to would
    /// hold the same value in every row forever.</summary>
    public static readonly IReadOnlySet<string> BaseKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "id", "created_at", "created_by", "updated_at", "updated_by", "deleted_at", "record_state",
    };

    /// <summary>Base fields the standalone target drops entirely.</summary>
    public static readonly IReadOnlySet<string> DroppedKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "company_id", "app_id",
    };

    public FieldModel(JsonObject json, string entityKey)
    {
        Json = json;
        EntityKey = entityKey;
        Key = AppModel.Str(json["key"]) ?? "field";
        Label = AppModel.Str(json["label"]) ?? Key;
        Type = AppModel.Str(json["type"]) ?? "text";
        Required = AppModel.Bool(json["required"]);
        Unique = AppModel.Bool(json["unique"]);
        Indexed = AppModel.Bool(json["indexed"]);
        ReadOnly = AppModel.Bool(json["readOnly"]);
        HideOnCreate = AppModel.Bool(json["hideOnCreate"]);
        System = AppModel.Bool(json["system"]);
        Role = AppModel.Str(json["role"]);
        TargetEntity = AppModel.Str(json["targetEntity"]);
        TargetApp = AppModel.Str(json["targetApp"]);
        OnDelete = AppModel.Str(json["onDelete"]);
        Help = AppModel.Str(json["help"]);
        Unit = AppModel.Str(json["unit"]);
        Currency = AppModel.Str(json["currency"]);
        Default = json["default"];
        Computed = json["computed"] as JsonObject;
        Options = [.. AppModel.Arr(json["options"]).OfType<JsonObject>()];
    }

    public JsonObject Json { get; }
    public string EntityKey { get; }
    public string Key { get; }
    public string Label { get; }
    public string Type { get; }
    public bool Required { get; }
    public bool Unique { get; }
    public bool Indexed { get; }
    public bool ReadOnly { get; }
    public bool HideOnCreate { get; }
    public bool System { get; }
    public string? Role { get; }
    public string? TargetEntity { get; }
    public string? TargetApp { get; }
    public string? OnDelete { get; }
    public string? Help { get; }
    public string? Unit { get; }
    public string? Currency { get; }
    public JsonNode? Default { get; }
    public JsonObject? Computed { get; }
    public IReadOnlyList<JsonObject> Options { get; }

    public bool IsBase => BaseKeys.Contains(Key);
    public bool IsReference => Type == "reference";

    /// <summary>
    /// The C# property this field maps to.
    ///
    /// <para>Usually the key in Pascal case. The AUDIT columns are the exception: the runtime
    /// declares them on <c>IHasTrackingFields</c> under names of its own, so <c>updated_at</c> is
    /// <c>LastModified</c> and no entity has a property called <c>UpdatedAt</c> to read. Anything
    /// that assumed otherwise produced code that did not compile — which is how this was found, from
    /// <c>days_between(created_at, resolved_at)</c> in a computed field.</para>
    ///
    /// <para>The same four-line mapping also appears in the entity, configuration and schema
    /// emitters, keyed on the field key rather than on this. Worth collapsing onto this property one
    /// day; not worth doing in the middle of another change.</para>
    /// </summary>
    public string PropertyName => Key switch
    {
        "created_at" => "Created",
        "created_by" => "CreatedBy",
        "updated_at" => "LastModified",
        "updated_by" => "LastModifiedBy",
        "id" => "Id",
        _ => Naming.Property(Key, EntityKey),
    };
    public string Column => Naming.Column(Key);

    /// <summary>
    /// The C# type, and whether it is nullable.
    ///
    /// <para>Optional means nullable, and that is not a style choice: a definition's optional field
    /// has no value until somebody enters one, and representing "not entered" as zero or as an empty
    /// string would make a sum wrong and a filter lie.</para>
    /// </summary>
    public string ClrType => Type switch
    {
        "integer" => "long",
        "decimal" or "money" => "decimal",
        "boolean" => "bool",
        "date" => "DateOnly",
        "datetime" => "DateTimeOffset",
        "multiselect" => "List<string>",
        "json" => "JsonDocument",
        // text, longtext, email, url, phone, select, reference, attachment — all text in the data
        // plane. A reference holds the target record's id, which is a string.
        _ => "string",
    };

    /// <summary>A value type that has to be written <c>long?</c> when the field is optional.</summary>
    public bool IsValueType => ClrType is "long" or "decimal" or "bool" or "DateOnly" or "DateTimeOffset";

    /// <summary>A list is never null — an empty selection is an empty list, not a missing one, and
    /// making the caller check for both would put a null test at every use.</summary>
    public bool IsCollection => ClrType == "List<string>";

    /// <summary>The declared type as it appears in generated source.</summary>
    public string DeclaredType => (Required, IsCollection) switch
    {
        (_, true) => "List<string>",
        (true, _) => ClrType,
        (false, _) => ClrType + "?",
    };

    /// <summary>What a fresh instance starts as. Required strings start empty rather than null so
    /// that the nullable analysis holds without a constructor.</summary>
    public string? Initialiser => (Required, ClrType, IsCollection) switch
    {
        (_, _, true) => "[]",
        (true, "string", _) => "\"\"",
        _ => null,
    };
}

/// <summary>A saved query over one entity: which rows, in what order, showing which columns.</summary>
public sealed class ViewModel
{
    public ViewModel(JsonObject json)
    {
        Json = json;
        Key = AppModel.Str(json["key"]) ?? "view";
        Label = AppModel.Str(json["label"]) ?? Key;
        Type = AppModel.Str(json["type"]) ?? "table";
        Entity = AppModel.Str(json["entity"]);
        Filters = [.. AppModel.Arr(json["filters"]).OfType<JsonObject>()];
        Sort = [.. AppModel.Arr(json["sort"]).OfType<JsonObject>()];
        Config = json["config"] as JsonObject;
    }

    public JsonObject Json { get; }
    public string Key { get; }
    public string Label { get; }
    public string Type { get; }
    public string? Entity { get; }
    public IReadOnlyList<JsonObject> Filters { get; }
    public IReadOnlyList<JsonObject> Sort { get; }
    public JsonObject? Config { get; }

    public IReadOnlyList<string> Columns =>
        [.. AppModel.Arr(Config?["columns"]).Select(c => AppModel.Str(c)).Where(c => c is not null).Select(c => c!)];
}

/// <summary>A screen in the navigation.</summary>
public sealed class PageModel
{
    public PageModel(JsonObject json)
    {
        Json = json;
        Key = AppModel.Str(json["key"]) ?? "page";
        Label = AppModel.Str(json["label"]) ?? Key;
        Icon = AppModel.Str(json["icon"]);
        Entity = AppModel.Str(json["entity"]);
        Blocks = AppModel.Arr(json["blocks"]);
    }

    public JsonObject Json { get; }
    public string Key { get; }
    public string Label { get; }
    public string? Icon { get; }
    public string? Entity { get; }
    public JsonArray Blocks { get; }

    public string ComponentName => Naming.Pascal(Key) + "Page";

    /// <summary>The URL this page lives at. The key, not an index, so a link somebody bookmarks
    /// survives a page being added above it.</summary>
    public string Route => "/" + Naming.Sanitise(Key).Replace('_', '-');
}

/// <summary>A record's lifecycle: the states it can be in and the moves between them.</summary>
public sealed class ProcessModel
{
    public ProcessModel(JsonObject json)
    {
        Json = json;
        Key = AppModel.Str(json["key"]) ?? "process";
        Entity = AppModel.Str(json["entity"]) ?? "";
        StateField = AppModel.Str(json["stateField"]) ?? "status";
        InitialState = AppModel.Str(json["initialState"]);
        States = [.. AppModel.Arr(json["states"]).OfType<JsonObject>()];
        Transitions = [.. AppModel.Arr(json["transitions"]).OfType<JsonObject>()];
    }

    public JsonObject Json { get; }
    public string Key { get; }
    public string Entity { get; }
    public string StateField { get; }
    public string? InitialState { get; }
    public IReadOnlyList<JsonObject> States { get; }
    public IReadOnlyList<JsonObject> Transitions { get; }

    /// <summary>The transition a command performs, if any command performs one. A command not
    /// named by a transition changes fields without moving the record.</summary>
    public JsonObject? TransitionForCommand(string commandKey) =>
        Transitions.FirstOrDefault(t => AppModel.Str(t["command"]) == commandKey);
}

/// <summary>Something a person can do to a record beyond editing its fields.</summary>
public sealed class CommandModel
{
    public CommandModel(JsonObject json)
    {
        Json = json;
        Key = AppModel.Str(json["key"]) ?? "command";
        Label = AppModel.Str(json["label"]) ?? Key;
        Entity = AppModel.Str(json["entity"]) ?? "";
        Style = AppModel.Str(json["style"]);
        Icon = AppModel.Str(json["icon"]);
        SuccessMessage = AppModel.Str(json["successMessage"]);
        Confirm = json["confirm"] as JsonObject;
        Input = json["input"] as JsonObject;
        Guard = json["guard"] as JsonObject;
        Effects = [.. AppModel.Arr(json["effects"]).OfType<JsonObject>()];
        Placements = [.. AppModel.Arr(json["placements"]).Select(p => AppModel.Str(p)).Where(p => p is not null).Select(p => p!)];
        Sets = [.. AppModel.Arr(json["set"]).OfType<JsonObject>()];
    }

    public JsonObject Json { get; }
    public string Key { get; }
    public string Label { get; }
    public string Entity { get; }
    public string? Style { get; }
    public string? Icon { get; }
    public string? SuccessMessage { get; }
    public JsonObject? Confirm { get; }
    public JsonObject? Input { get; }
    public JsonObject? Guard { get; }
    public IReadOnlyList<JsonObject> Effects { get; }
    public IReadOnlyList<string> Placements { get; }

    /// <summary>Fields the command writes itself, from its own <c>set</c> list.</summary>
    public IReadOnlyList<JsonObject> Sets { get; }

    /// <summary>Fields the caller must supply when running it.</summary>
    public IReadOnlyList<string> InputFields =>
        [.. AppModel.Arr(Input?["fields"]).Select(f => AppModel.Str(f)).Where(f => f is not null).Select(f => f!)];

    public IReadOnlyList<string> RequiredInputFields =>
        [.. AppModel.Arr(Input?["required"]).Select(f => AppModel.Str(f)).Where(f => f is not null).Select(f => f!)];
}

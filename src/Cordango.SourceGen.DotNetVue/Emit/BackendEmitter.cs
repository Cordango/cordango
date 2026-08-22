// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.Definition;
using Cordango.SourceGen.DotNetVue.Model;

namespace Cordango.SourceGen.DotNetVue.Emit;

/// <summary>
/// The files that wire an application's entities into the runtime: the context, the descriptors, the
/// registration, the controllers and the compiled permission rules.
///
/// <para>All of it is explicit. There is no scan, no convention and no attribute that a startup
/// routine goes looking for, so "what does this application register" is answered by reading one
/// file rather than by reasoning about what reflection would have found.</para>
/// </summary>
public static class BackendEmitter
{
    /// <summary>The <c>DbContext</c> — the whole model, listed.</summary>
    public static GeneratedFile DbContext(AppModel app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var source = new Source();
        source.Line("using Cordango.Standalone.Data;");
        source.Line("using Cordango.Standalone.Directory;");
        source.Line("using Cordango.Standalone.Notifications;");
        source.Line("using Cordango.Standalone.Preferences;");
        source.Line("using Cordango.Standalone.Records;");
        source.Line("using Microsoft.EntityFrameworkCore;");
        source.Line($"using {app.Namespace}.Entities;");
        source.Line();
        source.Line($"namespace {app.Namespace}.Data;");
        source.Line();
        source.Line("/// <summary>");
        source.Lines(Doc.Summary($"Everything {app.Name} stores.", null));
        source.Line("///");
        source.Line("/// <para>The list below is the whole model. There is no assembly scan and no convention that");
        source.Line("/// finds entities behind your back, so this file answers \"what is in the database\" by being");
        source.Line("/// read.</para>");
        source.Line("/// </summary>");
        source.Open($"public sealed class AppDbContext : CordangoDbContext");
        source.Line("public AppDbContext(DbContextOptions<AppDbContext> options, ICurrentUser user, IClock clock)");
        source.Indent().Line(": base(options, user, clock) { }").Outdent();
        source.Line();

        foreach (var entity in app.Entities)
            source.Line($"public DbSet<{entity.TypeName}> {Naming.Pascal(entity.Key)}Records => Set<{entity.TypeName}>();");

        source.Line();
        source.Open("protected override void ConfigureModel(ModelBuilder builder)");
        source.Line("// People, departments, groups, organizations and contacts. Every application gets these:");
        source.Line("// they are what a reference to a person or a customer points at.");
        source.Line("builder.AddDirectory();");
        source.Line();
        source.Line("// Each person's own column layouts, keyed by who they are. Never shared.");
        source.Line("builder.AddPreferences();");
        source.Line();
        source.Line("// What a command told somebody about.");
        source.Line("builder.AddNotifications();");
        source.Line();
        source.Line($"// {app.Name}'s own entities, in definition order.");
        foreach (var entity in app.Entities)
            source.Line($"builder.ApplyConfiguration(new {entity.TypeName}Configuration());");
        source.Close();
        source.Close();

        return new GeneratedFile("api/Data/AppDbContext.cs", source.ToString());
    }

    /// <summary>
    /// One descriptor per entity: the field keys the wire uses, and the assignment behind each.
    ///
    /// <para>Delegates rather than reflection, so a renamed property stops the build here instead of
    /// producing an update that silently ignores the field.</para>
    /// </summary>
    public static GeneratedFile Descriptors(AppModel app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var source = new Source();
        source.Line("using Cordango.Standalone.Data;");
        source.Line($"using {app.Namespace}.Entities;");
        source.Line();
        source.Line($"namespace {app.Namespace}.Data;");
        source.Line();
        source.Line("/// <summary>");
        source.Line("/// What the runtime knows about each entity: its key, its label, and how to move one field's");
        source.Line("/// value from one instance to another.");
        source.Line("///");
        source.Line("/// <para>Only the application's OWN fields are listed. The id and the audit columns are");
        source.Line("/// deliberately absent: a client that could name <c>created_by</c> in a payload could claim");
        source.Line("/// somebody else wrote the row.</para>");
        source.Line("/// </summary>");
        source.Open("public static class AppDescriptors");

        var first = true;
        foreach (var entity in app.Entities)
        {
            if (!first) source.Line();
            first = false;

            // Suffixed, because a static member named after its own type argument SHADOWS the type
            // inside the class — nameof(ExpenseClaim.Amount) then resolves against the descriptor
            // rather than the entity, and the error names a property nobody wrote.
            source.Line($"public static readonly RecordDescriptor<{entity.TypeName}> {entity.TypeName}Descriptor = new(");
            source.Indent();
            source.Line($"{Naming.Literal(entity.Key)},");
            source.Line($"{Naming.Literal(entity.Label)},");
            source.Line("[");
            source.Indent();

            foreach (var field in entity.AuthoredFields)
                source.Line($"new({Naming.Literal(field.Key)}, nameof({entity.TypeName}.{field.PropertyName}), "
                    + $"(from, to) => to.{field.PropertyName} = from.{field.PropertyName}),");

            source.Outdent();
            source.Line("]);");
            source.Outdent();
        }

        source.Close();
        return new GeneratedFile("api/Data/AppDescriptors.cs", source.ToString());
    }

    /// <summary>Registration: entities, descriptors, hooks and the permission rules.</summary>
    public static GeneratedFile Setup(AppModel app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Only the entities that actually need one get a hook, so an application where none does
        // has no Hooks namespace to import — and importing a namespace that does not exist is a
        // compile error, not a harmless line. task-manager was that application.
        var hooked = app.Entities.Any(e => AutoFields(app, e) is not null || Computed(app, e) is not null);

        var source = new Source();
        source.Line("using Cordango.Standalone.Commands;");
        source.Line("using Cordango.Standalone.Hooks;");
        source.Line("using Cordango.Standalone.Hosting;");
        source.Line("using Cordango.Standalone.Security;");
        source.Line($"using {app.Namespace}.Commands;");
        source.Line($"using {app.Namespace}.Entities;");
        if (hooked) source.Line($"using {app.Namespace}.Hooks;");
        source.Line($"using {app.Namespace}.Data;");
        source.Line($"using {app.Namespace}.Security;");
        source.Line($"using {app.Namespace}.Workflows;");
        source.Line();
        source.Line($"namespace {app.Namespace};");
        source.Line();
        source.Line("/// <summary>");
        source.Lines(Doc.Summary($"{app.Name}'s entities, hooks and permissions, registered.", null));
        source.Line("///");
        source.Line("/// <para>One place, one line per thing, in the order the definition lists them — which is why");
        source.Line("/// this application starts up the same way twice. Regenerating replaces this file; put your");
        source.Line("/// own registrations in a file beside it and call it from <c>Program.cs</c>.</para>");
        source.Line("/// </summary>");
        source.Open("public static class AppSetup");
        source.Open("public static IServiceCollection AddApp(this IServiceCollection services)");
        source.Line("// The definition's roles, commands and workflows, compiled in.");
        source.Line("services.AddSingleton(AppRoles.Rules);");
        source.Line("services.AddSingleton(AppCommands.Catalogue);");
        source.Line("services.AddSingleton(AppWorkflows.Catalogue);");
        source.Line();

        foreach (var entity in app.Entities)
        {
            source.Line($"services.AddRecord(AppDescriptors.{entity.TypeName}Descriptor);");

            if (AutoFields(app, entity) is not null)
                source.Line($"services.AddScoped<IBeforeCreate<{entity.TypeName}>, {entity.TypeName}AutoFields>();");

            if (Computed(app, entity) is not null)
            {
                source.Line($"services.AddScoped<IBeforeCreate<{entity.TypeName}>, {entity.TypeName}ComputedFields>();");
                source.Line($"services.AddScoped<IBeforeUpdate<{entity.TypeName}>, {entity.TypeName}ComputedFields>();");
            }
        }

        source.Line();
        source.Line("return services;");
        source.Close();
        source.Close();

        return new GeneratedFile("api/AppSetup.cs", source.ToString());
    }

    /// <summary>
    /// The definition's commands, as compiled data.
    ///
    /// <para>A command's rules travel with it: which states it may run from, what input it needs,
    /// what it writes. The runtime enforces those from this table rather than from a controller
    /// somebody wrote a second copy of them into.</para>
    /// </summary>
    public static GeneratedFile Commands(AppModel app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var source = new Source();
        source.Line("using Cordango.Standalone.Commands;");
        source.Line("using Cordango.Standalone.Conditions;");
        source.Line();
        source.Line($"namespace {app.Namespace}.Commands;");
        source.Line();
        source.Line($"/// <summary>Everything a person can do to a {app.Name} record beyond editing its fields.</summary>");
        source.Open("public static class AppCommands");
        source.Line("public static readonly AppCommandCatalogue Catalogue = new(");
        source.Indent();
        source.Line("[");
        source.Indent();

        foreach (var command in app.Commands)
        {
            var process = app.ProcessFor(command.Entity);
            var transition = process?.TransitionForCommand(command.Key);

            var from = transition is null
                ? []
                : AppModel.Arr(transition["from"]).Select(f => AppModel.Str(f)).Where(f => f is not null).Select(f => f!).ToList();
            var to = transition is null ? null : AppModel.Str(transition["to"]);

            source.Line($"new CommandDefinition({Naming.Literal(command.Key)}, {Naming.Literal(command.Label)}, {Naming.Literal(command.Entity)},");
            source.Indent();
            source.Line($"StateField: {(transition is null ? "null" : Naming.Literal(process!.StateField))},");
            source.Line($"FromStates: [{string.Join(", ", from.Select(Naming.Literal))}],");
            source.Line($"ToState: {(to is null ? "null" : Naming.Literal(to))},");
            source.Line($"InputFields: [{string.Join(", ", command.InputFields.Select(Naming.Literal))}],");
            source.Line($"RequiredInputFields: [{string.Join(", ", command.RequiredInputFields.Select(Naming.Literal))}],");

            var sets = command.Sets
                .Select(set => (Field: AppModel.Str(set["field"]), Value: set["value"]?.ToString()))
                .Where(s => s.Field is not null)
                .Select(s => $"new CommandSet({Naming.Literal(s.Field!)}, {Naming.Literal(s.Value)})");

            source.Line($"Sets: [{string.Join(", ", sets)}],");
            source.Line($"SuccessMessage: {Naming.Literal(command.SuccessMessage)},");

            // Only `notify` — the others need a mail server or an outbound HTTP client, and an
            // application that silently sent nothing would be worse than one that says so. The
            // generator reports the rest as CORD2303.
            var notifications = command.Effects
                .Where(e => AppModel.Str(e["type"]) == "notify")
                .Select(e => "new CommandNotification("
                    + $"{Naming.Literal(AppModel.Str(e["to"]) ?? "")}, "
                    + $"{Naming.Literal(AppModel.Str(e["title"]) ?? command.Label)}, "
                    + $"{Naming.Literal(AppModel.Str(e["message"]))}, "
                    + $"{Naming.Literal(AppModel.Str(e["link"]))})");

            source.Line($"Notifications: [{string.Join(", ", notifications)}],");

            // The guard, as the last argument, because it is the one a reader most often wants to
            // find: everything above it is what the command DOES, and this is when it may.
            ConditionEmitter.TryEmit(command.Json["when"], out var guard);
            source.Line($"When: {guard}),");
            source.Outdent();
        }

        source.Outdent();
        source.Line("]);");
        source.Outdent();
        source.Close();

        return new GeneratedFile("api/Commands/AppCommands.cs", source.ToString());
    }

    /// <summary>
    /// The fields the runtime fills in rather than the person.
    ///
    /// <para>The compiler marks them: <c>submitted_by</c> and <c>owner</c> are whoever is writing,
    /// <c>submitted_at</c> and <c>logged_at</c> are when. They are hidden from every form, so if
    /// nothing filled them they would simply be null — and "who submitted this" being null on every
    /// row is the sort of emptiness that looks like a data problem rather than a missing
    /// feature.</para>
    ///
    /// <para>A hook rather than a default on the column, because the answer depends on the request.
    /// Returns null for an entity with no such fields, so nothing is emitted that does nothing.</para>
    /// </summary>
    public static GeneratedFile? AutoFields(AppModel app, EntityModel entity)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(entity);

        var auto = entity.AuthoredFields
            .Where(f => AppModel.Str(f.Json["auto"]) is "currentUser" or "currentTime")
            .ToList();

        if (auto.Count == 0) return null;

        var source = new Source();
        source.Line("using Cordango.Standalone.Hooks;");
        source.Line($"using {app.Namespace}.Entities;");
        source.Line();
        source.Line($"namespace {app.Namespace}.Hooks;");
        source.Line();
        source.Line($"/// <summary>Fills in the {entity.Label} fields the definition says the runtime owns.</summary>");
        source.Open($"public sealed class {entity.TypeName}AutoFields : IBeforeCreate<{entity.TypeName}>");
        source.Open($"public Task BeforeCreateAsync({entity.TypeName} record, RecordContext context, CancellationToken ct)");

        foreach (var field in auto)
        {
            var kind = AppModel.Str(field.Json["auto"]);
            source.Line($"// {field.Label}: {(kind == "currentUser" ? "whoever is writing" : "when they wrote it")}.");

            if (kind == "currentUser")
            {
                source.Line($"record.{field.PropertyName} ??= context.User.PersonId;");
            }
            else if (field.Type == "date")
            {
                source.Line($"record.{field.PropertyName} ??= DateOnly.FromDateTime(context.Clock.UtcNow.UtcDateTime);");
            }
            else
            {
                source.Line($"record.{field.PropertyName} ??= context.Clock.UtcNow;");
            }
        }

        source.Line();
        source.Line("return Task.CompletedTask;");
        source.Close();
        source.Close();

        return new GeneratedFile($"api/Hooks/{entity.TypeName}AutoFields.cs", source.ToString());
    }

    /// <summary>
    /// One entity's computed fields, as methods, plus the order to run them in.
    ///
    /// <para>Emitted only when the entity has some. A file full of nothing is a file somebody has to
    /// open to discover it says nothing.</para>
    /// </summary>
    public static GeneratedFile? Computed(AppModel app, EntityModel entity)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(entity);

        var ordered = Ordered(entity).ToList();
        if (ordered.Count == 0) return null;

        var source = new Source();
        source.Line("using Cordango.Standalone.Records;");
        source.Line($"using {app.Namespace}.Entities;");
        source.Line();
        source.Line($"namespace {app.Namespace}.Computed;");
        source.Line();
        source.Line("/// <summary>");
        source.Lines(Doc.Summary($"The {entity.Label} figures the application works out for itself.", null));
        source.Line("///");
        source.Line("/// <para>One method per computed field, and <c>Apply</c> runs them in the order their");
        source.Line("/// dependencies require — a total before the percentage that divides by it. Read them; they are");
        source.Line("/// ordinary arithmetic, and a wrong figure is a breakpoint away rather than an expression");
        source.Line("/// string somewhere in a table.</para>");
        source.Line("///");
        source.Line("/// <para>Regenerating replaces this file. A figure the definition does not describe belongs in a");
        source.Line("/// hook of your own beside it.</para>");
        source.Line("/// </summary>");
        source.Open($"public static class {entity.TypeName}Computed");

        foreach (var (field, expression) in ordered)
        {
            source.Line($"/// <summary>{Xml(field.Label)}: <c>{Xml(AppModel.Str(field.Computed?["expr"]) ?? "")}</c></summary>");
            source.Line($"public static {Result(field)} {Naming.Pascal(field.Key)}({entity.TypeName} r) =>");
            source.Indent();
            source.Line(expression + ";");
            source.Outdent();
            source.Line();
        }

        source.Line("/// <summary>Every computed field on this record, in dependency order.</summary>");
        source.Open($"public static void Apply({entity.TypeName} r)");

        foreach (var (field, _) in ordered)
            source.Line($"r.{field.PropertyName} = {Cast(field)}{Naming.Pascal(field.Key)}(r);");

        source.Close();
        source.Close();

        return new GeneratedFile($"api/Computed/{entity.TypeName}Computed.cs", source.ToString());
    }

    /// <summary>What a computed method returns. Numbers are worked out as <c>decimal?</c> whatever
    /// the column holds — an average of integers is not an integer, and rounding on the way through
    /// every intermediate step would make the answer depend on how the author happened to split the
    /// expression.</summary>
    private static string Result(FieldModel field) =>
        field.Type == "boolean" ? "bool?" : "decimal?";

    /// <summary>The narrowing back to the column's own type, once, at the end. Explicit rather than
    /// implicit because <c>decimal</c> to <c>long</c> loses the fraction and that has to be a
    /// decision somebody can see in the generated line.</summary>
    private static string Cast(FieldModel field) =>
        field.ClrType switch
        {
            "decimal" => "",
            "bool" => "",
            _ => $"({field.ClrType}?)",
        };

    /// <summary>
    /// The computed fields, in an order where nothing is worked out before what it reads.
    ///
    /// <para>A depth-first walk over each field's own dependencies. <c>progress</c> divides by
    /// <c>total_tasks</c>, so <c>total_tasks</c> has to have a value first; run them in declaration
    /// order instead and a percentage computes against the previous save's total, which is the kind
    /// of wrong that looks right until somebody checks.</para>
    ///
    /// <para>A cycle drops out rather than looping — the gate refuses one at author time, and a
    /// generator that hung on a definition would be a worse way to find out.</para>
    /// </summary>
    private static IEnumerable<(FieldModel Field, string Expression)> Ordered(EntityModel entity)
    {
        var computed = entity.AuthoredFields
            .Where(f => f.Computed?["expr"] is not null)
            .ToDictionary(f => f.Key, f => f, StringComparer.Ordinal);

        var done = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var order = new List<FieldModel>();

        foreach (var key in computed.Keys.OrderBy(k => k, StringComparer.Ordinal)) Visit(key);

        foreach (var field in order)
            if (ComputedEmitter.Expression(entity, field) is { } expression)
                yield return (field, expression);

        void Visit(string key)
        {
            if (done.Contains(key) || !visiting.Add(key)) return;

            foreach (var dependency in ComputedExpr
                         .LocalIdentifiers(AppModel.Str(computed[key].Computed?["expr"]))
                         .Where(computed.ContainsKey)
                         .OrderBy(k => k, StringComparer.Ordinal))
                Visit(dependency);

            visiting.Remove(key);
            if (done.Add(key)) order.Add(computed[key]);
        }
    }

    /// <summary>A label or an expression inside a doc comment. <c>a &lt; b</c> in an expression is
    /// an unclosed tag to the XML documentation compiler, and a warning on every generated
    /// build.</summary>
    private static string Xml(string text) => text
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);

    /// <summary>
    /// The hook that keeps an entity's computed fields up to date.
    ///
    /// <para>Before the write rather than after, so the row that is saved already carries its own
    /// figures — one write instead of two, and no window in which the record exists with a total
    /// that has not been worked out. A client that reads it back immediately gets the right
    /// number.</para>
    ///
    /// <para>Both create and update: a total on a new record is as real as one on an edited record,
    /// and forgetting create is how a list ends up with blanks in the first row only.</para>
    /// </summary>
    public static GeneratedFile? ComputedHook(AppModel app, EntityModel entity)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(entity);

        if (Computed(app, entity) is null) return null;

        var source = new Source();
        source.Line("using Cordango.Standalone.Hooks;");
        source.Line($"using {app.Namespace}.Computed;");
        source.Line($"using {app.Namespace}.Entities;");
        source.Line();
        source.Line($"namespace {app.Namespace}.Hooks;");
        source.Line();
        source.Line($"/// <summary>Works out {entity.Label}'s own figures before every write.</summary>");
        source.Open($"public sealed class {entity.TypeName}ComputedFields "
            + $": IBeforeCreate<{entity.TypeName}>, IBeforeUpdate<{entity.TypeName}>");

        source.Open($"public Task BeforeCreateAsync({entity.TypeName} record, RecordContext context, CancellationToken ct)");
        source.Line($"{entity.TypeName}Computed.Apply(record);");
        source.Line("return Task.CompletedTask;");
        source.Close();
        source.Line();

        source.Open($"public Task BeforeUpdateAsync({entity.TypeName} record, {entity.TypeName} before, "
            + "RecordContext context, CancellationToken ct)");
        source.Line($"{entity.TypeName}Computed.Apply(record);");
        source.Line("return Task.CompletedTask;");
        source.Close();

        source.Close();

        return new GeneratedFile($"api/Hooks/{entity.TypeName}ComputedFields.cs", source.ToString());
    }

    /// <summary>One controller per entity. Six lines each, and they are six lines you can read,
    /// route-match and set a breakpoint in.</summary>
    public static GeneratedFile Controller(AppModel app, EntityModel entity)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(entity);

        var source = new Source();
        source.Line("using Cordango.Standalone.Data;");
        source.Line("using Cordango.Standalone.Http;");
        source.Line("using Cordango.Standalone.Records;");
        source.Line("using Cordango.Standalone.Security;");
        source.Line("using Microsoft.AspNetCore.Mvc;");
        source.Line($"using {app.Namespace}.Entities;");
        source.Line();
        source.Line($"namespace {app.Namespace}.Controllers;");
        source.Line();
        source.Line($"/// <summary>{entity.LabelPlural}. CRUD, with the definition's roles enforced on every route.");
        source.Line("/// Add your own endpoints in a class of your own — this file is regenerated.</summary>");
        source.Line($"[Route({Naming.Literal("api/" + entity.Key)})]");
        source.Open($"public sealed class {entity.TypeName}Controller : RecordsController<{entity.TypeName}>");
        source.Line($"public {entity.TypeName}Controller(IRecordStore<{entity.TypeName}> store, AppPermissions permissions, ICurrentUser user)");
        source.Indent().Line(": base(store, permissions, user) { }").Outdent();
        source.Close();

        return new GeneratedFile($"api/Controllers/{entity.TypeName}Controller.cs", source.ToString());
    }

    /// <summary>
    /// The definition's <c>roles</c>, as compiled C#.
    ///
    /// <para>Compiled in rather than read from a file at startup, so the rules cannot be edited into
    /// a state the definition never described without editing source, and so a deployment cannot
    /// half-apply them by shipping a stale copy of one file.</para>
    /// </summary>
    public static GeneratedFile Permissions(AppModel app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var source = new Source();
        source.Line("using Cordango.Standalone.Security;");
        source.Line();
        source.Line($"namespace {app.Namespace}.Security;");
        source.Line();
        source.Line("/// <summary>");
        source.Line("/// Who may do what, from the definition's roles.");
        source.Line("///");
        source.Line("/// <para>Four rules decide an answer: any role the caller holds that allows an operation");
        source.Line("/// allows it; within one role a grant naming the entity replaces that role's <c>*</c> grant;");
        source.Line("/// a field's answer is that role's override if it has one, else that role's entity default,");
        source.Line("/// and only then unioned across roles; and commands are denied unless a grant names them.</para>");
        source.Line("/// </summary>");
        // Named AppRoles rather than AppPermissions: the runtime type it CONSTRUCTS is called
        // AppPermissions, and a generated class of the same name in a namespace the same file
        // imports is an ambiguous reference — one that only appears once an application actually has
        // roles, which is the worst time to find out.
        source.Open("public static class AppRoles");

        if (app.Roles.Count == 0)
        {
            source.Line("/// <summary>This definition declares no roles. Anyone signed in may read; nobody may");
            source.Line("/// write. Nobody signed in gets nothing at all.</summary>");
            source.Line("public static readonly AppPermissions Rules =");
            source.Indent().Line("AppPermissions.None;").Outdent();
            source.Close();
            return new GeneratedFile("api/Security/AppRoles.cs", source.ToString());
        }

        source.Line("public static readonly AppPermissions Rules = new(");
        source.Indent();
        source.Line("[");
        source.Indent();

        foreach (var role in app.Roles)
        {
            var key = AppModel.Str(role["key"]) ?? "role";
            var label = AppModel.Str(role["label"]);
            if (label is not null) source.Line($"// {label}");

            source.Line($"new RoleDefinition({Naming.Literal(key)},");
            source.Indent();
            source.Line("[");
            source.Indent();

            foreach (var grant in AppModel.Arr(role["grants"]).OfType<JsonObject>())
                EmitGrant(source, grant);

            source.Outdent();
            source.Line("]),");
            source.Outdent();
        }

        source.Outdent();
        source.Line("]);");
        source.Outdent();
        source.Close();

        return new GeneratedFile("api/Security/AppRoles.cs", source.ToString());
    }

    private static void EmitGrant(Source source, JsonObject grant)
    {
        var entity = AppModel.Str(grant["entity"]) ?? "*";
        var create = AppModel.Bool(grant["create"]);
        var read = AppModel.Bool(grant["read"]);
        var update = AppModel.Bool(grant["update"]);
        var delete = AppModel.Bool(grant["delete"]);

        var overrides = AppModel.Arr(grant["fieldOverrides"]).OfType<JsonObject>().ToList();
        var commands = AppModel.Arr(grant["commands"])
            .Select(c => AppModel.Str(c))
            .Where(c => c is not null)
            .Select(c => c!)
            .ToList();

        var head = $"new EntityGrant({Naming.Literal(entity)}, {Lower(create)}, {Lower(read)}, {Lower(update)}, {Lower(delete)}";

        if (overrides.Count == 0 && commands.Count == 0)
        {
            source.Line(head + "),");
            return;
        }

        source.Line(head + ",");
        source.Indent();

        if (overrides.Count == 0)
        {
            source.Line("null,");
        }
        else
        {
            source.Line("[");
            source.Indent();
            foreach (var o in overrides)
            {
                var field = AppModel.Str(o["field"]) ?? "";
                source.Line($"new FieldOverride({Naming.Literal(field)}, {Nullable(o["read"])}, {Nullable(o["update"])}),");
            }
            source.Outdent();
            source.Line(commands.Count == 0 ? "]),": "],");
        }

        if (commands.Count > 0)
        {
            source.Line("[" + string.Join(", ", commands.Select(Naming.Literal)) + "]),");
        }
        else if (overrides.Count == 0)
        {
            source.Line("null),");
        }

        source.Outdent();
    }

    private static string Lower(bool value) => value ? "true" : "false";

    /// <summary>An override that says nothing about read or update falls through to the role's
    /// entity-level default, which is a different answer from saying false.</summary>
    private static string Nullable(JsonNode? node) => node?.GetValueKind() switch
    {
        System.Text.Json.JsonValueKind.True => "true",
        System.Text.Json.JsonValueKind.False => "false",
        _ => "null",
    };
}

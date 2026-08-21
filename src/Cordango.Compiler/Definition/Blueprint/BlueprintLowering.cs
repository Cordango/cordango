// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cordango.Definition.Blueprints;

/// <summary>
/// Turns an approved blueprint into an App Definition. <b>No model calls.</b> This is the step that
/// makes approval mean something: whatever the user agreed to is what gets built, because nothing
/// between here and the definition has an opinion.
///
/// <para>Three rules hold it to that:</para>
/// <list type="number">
///   <item>No silent fallback. An element that cannot be lowered produces a <see
///   cref="LoweringDiagnostic"/> naming it, never a quiet omission or a guessed substitute.</item>
///   <item>Pure. Same blueprint in, byte-identical definition out — no clock, no randomness, no
///   ambient state. It is what makes the output reviewable and the identity map trustworthy.</item>
///   <item>Layout is policy, not content. The blueprint carries semantic intent (a board grouped by
///   this field, a table showing these columns); the block trees, view types and page structure are
///   decided here, by rules written down once.</item>
/// </list>
/// </summary>
public static class BlueprintLowering
{
    /// <summary>The App Definition schema version this lowering targets.</summary>
    public const string TargetSchemaVersion = "2.0";

    /// <summary>The definition version a first lowering produces. Later revisions get theirs from
    /// the compilation run, never from a model's idea of semantic versioning.</summary>
    public const string InitialAppVersion = "1.0.0";

    public static LoweringResult ToDefinition(Blueprint bp, string? appVersion = null)
    {
        var ctx = new Context(bp);

        var def = new JsonObject
        {
            ["schemaVersion"] = TargetSchemaVersion,
            ["key"] = bp.App.Key,
            ["name"] = bp.App.Name,
            ["version"] = appVersion ?? InitialAppVersion,
        };
        if (!string.IsNullOrWhiteSpace(bp.App.Description)) def["description"] = bp.App.Description;
        if (!string.IsNullOrWhiteSpace(bp.Intent.CoreJob))
            def["archetype"] = new JsonObject
            {
                ["kind"] = bp.Intent.ArchetypeKind,
                ["coreJob"] = bp.Intent.CoreJob,
            };

        // Commands are planned before anything is emitted: a transition names the command that backs
        // it, and a command names its entity, so both passes have to agree on keys decided once.
        ctx.PlanCommands();

        def["entities"] = Entities(ctx);
        if (Relations(ctx) is { Count: > 0 } relations) def["relations"] = relations;
        if (Processes(ctx) is { Count: > 0 } processes) def["processes"] = processes;
        if (Commands(ctx) is { Count: > 0 } commands) def["commands"] = commands;
        if (Roles(ctx) is { Count: > 0 } roles) def["roles"] = roles;
        var (views, pages) = Experience(ctx);
        if (views.Count > 0) def["views"] = views;
        if (pages.Count > 0) def["pages"] = pages;

        return new LoweringResult(def, ctx.BuildMap(), ctx.Diagnostics);
    }

    // ---- entities ---------------------------------------------------------------------------

    private static JsonArray Entities(Context ctx)
    {
        var entities = new JsonArray();
        foreach (var c in ctx.Bp.Concepts.Where(c => ConceptKinds.ProducesEntity(c.Kind)))
        {
            var entity = new JsonObject { ["key"] = c.Key, ["label"] = c.Name };
            if (!string.IsNullOrWhiteSpace(c.Plural)) entity["labelPlural"] = c.Plural;
            if (!string.IsNullOrWhiteSpace(c.Purpose)) entity["description"] = c.Purpose;
            if (!string.IsNullOrWhiteSpace(c.Icon)) entity["icon"] = c.Icon;

            // ALWAYS stated, never left to the default. The compiler infers 'config' for anything
            // that looks like a lookup list when kind is absent, which silently moved Candidates and
            // Positions out of the navigation and into a Configuration area — an approved decision
            // overridden by a downstream heuristic. An approved choice has to be said out loud.
            entity["kind"] = c.Kind switch
            {
                ConceptKinds.Config => "config",
                ConceptKinds.Settings => "settings",
                _ => "collection",
            };

            if (c.RecordLabel is { } label && ctx.Bp.Field(label.FieldId) is { } labelField)
                entity["displayField"] = labelField.Key;

            if (Ownership(ctx, c) is { } ownedBy) entity["ownedBy"] = ownedBy;

            entity["fields"] = Fields(ctx, c);
            if (Detail(ctx, c) is { } detail) entity["detail"] = detail;

            entities.Add(entity);
            ctx.MapEntity(c.Id, c.Key);
        }
        return entities;
    }

    /// <summary>A composed child lowers to <c>ownedBy</c>: no page, no navigation, no standalone
    /// list. <c>via</c> is the reference field the composition itself produced.</summary>
    private static JsonObject? Ownership(Context ctx, ConceptSpec c)
    {
        var composition = ctx.Bp.Relationships.FirstOrDefault(r => r.IsComposition && r.ToConceptId == c.Id);
        if (composition is null) return null;
        var parent = ctx.Bp.Concept(composition.FromConceptId);
        if (parent is null) return null;

        var owned = new JsonObject { ["parent"] = parent.Key, ["via"] = composition.Key };
        if (composition.ChildPresentation is { } presentation) owned["as"] = presentation;
        return owned;
    }

    private static JsonArray Fields(Context ctx, ConceptSpec c)
    {
        var fields = new JsonArray();

        foreach (var f in ctx.Bp.AllFields.Where(f => f.ConceptId == c.Id))
        {
            if (LowerField(ctx, c, f) is { } lowered)
            {
                fields.Add(lowered);
                ctx.MapField(f.Id, c.Key, f.Key);
            }
        }

        // Relationship-backed references: the side that stores the key carries the field.
        foreach (var r in ctx.Bp.Relationships.Where(r => r.OwningConceptId == c.Id))
        {
            var target = ctx.Bp.Concept(r.TargetConceptId);
            if (target is null)
            {
                ctx.Fail(r.Id, SpecLayers.Relationships, $"'{r.Label}' points at a concept that does not exist");
                continue;
            }
            var field = new JsonObject
            {
                ["key"] = r.Key,
                ["label"] = r.Label,
                ["type"] = FieldTypes.Reference,
                ["targetEntity"] = target.Key,
            };
            if (r.Required) field["required"] = true;
            field["onDelete"] = OnDelete(r.DeleteBehavior);
            fields.Add(field);
            ctx.MapField(r.Id, c.Key, r.Key);
        }

        // External references: the platform directory, or a core app addressed by SystemKey.
        foreach (var r in ctx.Bp.References.Where(r => r.OnConceptId == c.Id))
        {
            var external = ctx.Bp.Externals.FirstOrDefault(x => x.Id == r.ExternalConceptId);
            if (external is null)
            {
                ctx.Fail(r.Id, SpecLayers.Concepts, $"'{r.Label}' points at an external dataset that does not exist");
                continue;
            }
            var field = new JsonObject
            {
                ["key"] = r.Key,
                ["label"] = r.Label,
                ["type"] = FieldTypes.Reference,
                ["targetEntity"] = external.EntityKey,
                // A core app is addressed by its permanent SystemKey. Handles are disposable
                // URL-facing addresses that collide and get suffixed, so one must never end up here.
                ["targetApp"] = external.Source == ExternalSources.Core
                    ? external.CoreSystemKey ?? ""
                    : "platform",
            };
            if (r.Required) field["required"] = true;
            field["onDelete"] = OnDelete(r.DeleteBehavior);
            fields.Add(field);
            ctx.MapField(r.Id, c.Key, r.Key);
        }

        if (fields.Count == 0)
            ctx.Fail(c.Id, SpecLayers.Concepts, $"'{c.Name}' has no fields, and every entity needs at least one");

        return fields;
    }

    private static JsonObject? LowerField(Context ctx, ConceptSpec c, FieldSpec f)
    {
        var field = new JsonObject
        {
            ["key"] = f.Key,
            ["label"] = f.Label,
            ["type"] = f.Type,
        };
        if (f.Required) field["required"] = true;
        if (f.Unique) field["unique"] = true;
        if (f.Indexed) field["indexed"] = true;
        if (!string.IsNullOrWhiteSpace(f.Help)) field["help"] = f.Help;
        if (!string.IsNullOrWhiteSpace(f.Group)) field["group"] = f.Group;
        if (!string.IsNullOrWhiteSpace(f.Unit)) field["unit"] = f.Unit;
        if (!string.IsNullOrWhiteSpace(f.Currency)) field["currency"] = f.Currency;
        if (f.Precision is { } precision) field["precision"] = precision;
        if (f.Scale is { } scale) field["scale"] = scale;
        if (f.Default is { } dflt) field["default"] = JsonNode.Parse(dflt.GetRawText());

        var governed = ctx.Bp.Workflows.Any(w => w.ConceptId == c.Id && w.StatusFieldKey == f.Key);
        // The definition ties a process to its field through role:'status', not by name. Without it
        // the process has nothing to govern and the runtime cannot render a stepper.
        if (governed) field["role"] = "status";

        if (FieldTypes.IsChoice(f.Type) && !governed)
        {
            // A governed status field gets its options from the process, and authoring both is an
            // error in the definition — so this is the one field type whose values come from
            // somewhere else entirely.
            var options = Options(ctx, f);
            if (options is null) return null;
            field["options"] = options;
        }

        if (f.Computed is { } computed && LowerComputed(ctx, c, f, computed) is { } lowered)
            field["computed"] = lowered;

        return field;
    }

    private static JsonArray? Options(Context ctx, FieldSpec f)
    {
        var source = f.Options;
        if (f.OptionsFromConceptId is { } enumId)
        {
            var enumConcept = ctx.Bp.Concept(enumId);
            if (enumConcept is null)
            {
                ctx.Fail(f.Id, SpecLayers.Data, $"'{f.Label}' takes its values from a concept that does not exist");
                return null;
            }
            source = enumConcept.Options;
        }
        if (source.Count == 0)
        {
            ctx.Fail(f.Id, SpecLayers.Data, $"'{f.Label}' is a {f.Type} with no values to choose from");
            return null;
        }

        var options = new JsonArray();
        foreach (var o in source)
        {
            var option = new JsonObject { ["value"] = o.Value, ["label"] = o.Label };
            if (o.Phase is { } phase) option["phase"] = phase;
            options.Add(option);
        }
        return options;
    }

    private static JsonObject? LowerComputed(Context ctx, ConceptSpec c, FieldSpec f, ComputedSpec computed)
    {
        if (computed.Expr is { } expr)
        {
            // Expressions are authored with field IDS and lowered to keys, so renaming a field can
            // never silently break a formula.
            var substituted = expr;
            foreach (var id in BlueprintGate.ExprFieldIds(expr).Distinct())
            {
                if (ctx.Bp.Field(id) is not { } target)
                {
                    ctx.Fail(f.Id, SpecLayers.Data, $"'{f.Label}' is calculated from a field that does not exist");
                    return null;
                }
                substituted = substituted.Replace(id, target.Key, StringComparison.Ordinal);
            }
            return new JsonObject { ["expr"] = substituted };
        }

        if (computed.Rollup is not { } rollup) return null;

        var over = ctx.Bp.Concept(rollup.ConceptId);
        var via = ctx.Bp.Relationship(rollup.ViaRelationshipId);
        if (over is null || via is null)
        {
            ctx.Fail(f.Id, SpecLayers.Data, $"'{f.Label}' totals up records through a relationship that does not exist");
            return null;
        }

        var node = new JsonObject
        {
            ["entity"] = over.Key,
            ["via"] = via.Key,
            ["op"] = rollup.Op,
        };
        if (rollup.FieldId is { } aggId)
        {
            if (ctx.Bp.Field(aggId) is not { } agg)
            {
                ctx.Fail(f.Id, SpecLayers.Data, $"'{f.Label}' totals a field that does not exist");
                return null;
            }
            node["field"] = agg.Key;
        }
        if (Filters(ctx, rollup.Filters, f.Id, SpecLayers.Data) is { Count: > 0 } filters)
            node["filters"] = filters;

        return new JsonObject { ["rollup"] = node };
    }

    private static string OnDelete(string deleteBehavior) => deleteBehavior switch
    {
        DeleteBehaviors.Cascade => "cascade",
        DeleteBehaviors.SetNull => "setNull",
        _ => "restrict",
    };

    /// <summary>The record detail: the surface's own columns as a fields block, then a child block
    /// per composed concept. Layout policy, written once here rather than decided per app.</summary>
    private static JsonObject? Detail(Context ctx, ConceptSpec c)
    {
        var surface = ctx.Bp.Experience?.Surfaces
            .FirstOrDefault(s => s.Surface == SurfaceKinds.Detail && s.ConceptId == c.Id);
        if (surface is null) return null;

        var blocks = new JsonArray();
        if (surface.Columns.Count > 0)
        {
            var keys = new JsonArray();
            foreach (var col in surface.Columns)
                if (ctx.KeyOf(col) is { } key) keys.Add(key);
            if (keys.Count > 0) blocks.Add(new JsonObject { ["kind"] = "fields", ["fields"] = keys });
        }
        else
        {
            blocks.Add(new JsonObject { ["kind"] = "fields" });
        }

        foreach (var childId in surface.RelatedConceptIds)
        {
            var child = ctx.Bp.Concept(childId);
            var via = ctx.Bp.Relationships.FirstOrDefault(
                r => r.ToConceptId == childId && r.TargetConceptId == c.Id);
            if (child is null || via is null)
            {
                ctx.Fail(surface.Id, SpecLayers.Experience,
                    $"'{surface.Label}' shows related records that nothing connects to this one");
                continue;
            }
            var block = new JsonObject
            {
                ["kind"] = "child",
                ["entity"] = child.Key,
                ["via"] = via.Key,
                ["label"] = child.Plural ?? child.Name,
            };
            blocks.Add(block);
        }

        return blocks.Count > 0 ? new JsonObject { ["blocks"] = blocks } : null;
    }

    // ---- relations --------------------------------------------------------------------------

    private static JsonArray Relations(Context ctx)
    {
        var relations = new JsonArray();
        foreach (var r in ctx.Bp.Relationships)
        {
            // A to-one relationship is fully expressed by the reference field it already produced;
            // a relations entry would say the same thing twice.
            if (r.Cardinality == Cardinalities.OneToOne) continue;

            var from = ctx.Bp.Concept(r.FromConceptId);
            var to = ctx.Bp.Concept(r.ToConceptId);
            if (from is null || to is null) continue;   // already reported while lowering fields

            // Computed ONCE: the key allocator dedupes, so asking twice would hand back the
            // collision-broken variant the second time.
            var key = ctx.RelationKey(r);
            var relation = new JsonObject
            {
                ["key"] = key,
                ["label"] = r.Label,
                ["type"] = r.Cardinality == Cardinalities.ManyToMany ? "manyToMany" : "oneToMany",
                ["fromEntity"] = from.Key,
                ["toEntity"] = to.Key,
            };
            // oneToMany: the definition needs to know WHICH reference on the many side owns the key.
            if (r.Cardinality == Cardinalities.OneToMany) relation["inverseField"] = r.Key;

            relations.Add(relation);
            ctx.MapRelation(r.Id, key);
        }
        return relations;
    }

    // ---- behaviour --------------------------------------------------------------------------

    private static JsonArray Processes(Context ctx)
    {
        var processes = new JsonArray();
        foreach (var w in ctx.Bp.Workflows)
        {
            var concept = ctx.Bp.Concept(w.ConceptId);
            if (concept is null)
            {
                ctx.Fail(w.Id, SpecLayers.Workflows, $"'{w.Label}' governs a concept that does not exist");
                continue;
            }
            var initial = w.States.FirstOrDefault(s => s.Initial);
            if (initial is null)
            {
                ctx.Fail(w.Id, SpecLayers.Workflows, $"'{w.Label}' has no state for a new record to start in");
                continue;
            }

            var states = new JsonArray();
            foreach (var s in w.States)
            {
                var state = new JsonObject { ["key"] = s.Key, ["label"] = s.Label, ["phase"] = s.Phase };
                if (s.Terminal) state["terminal"] = true;
                if (s.Color is { } color) state["color"] = color;
                states.Add(state);
                ctx.MapState(s.Id, concept.Key, s.Key);
            }

            var transitions = new JsonArray();
            foreach (var t in w.Transitions)
            {
                var from = w.State(t.FromStateId);
                var to = w.State(t.ToStateId);
                if (from is null || to is null)
                {
                    ctx.Fail(t.Id, SpecLayers.Workflows, $"'{t.Label}' starts or ends at a state that does not exist");
                    continue;
                }
                var transition = new JsonObject
                {
                    ["key"] = t.Key,
                    ["label"] = t.Label,
                    // The definition lets one transition start from SEVERAL states. The blueprint
                    // models each move separately, which reads better in the wizard and lowers to a
                    // single-element list here.
                    ["from"] = new JsonArray(from.Key),
                    ["to"] = to.Key,
                };
                if (ctx.CommandKeyFor(t.Id) is { } commandKey) transition["command"] = commandKey;
                if (t.RequiresFieldIds.Count > 0)
                {
                    var required = new JsonArray();
                    foreach (var fid in t.RequiresFieldIds)
                        if (ctx.KeyOf(fid) is { } key) required.Add(key);
                    if (required.Count > 0) transition["requiredFields"] = required;
                }
                if (t.Guard is { } guard && LowerFilter(ctx, guard, t.Id, SpecLayers.Workflows) is { } when)
                    transition["when"] = when;

                transitions.Add(transition);
                ctx.MapTransition(t.Id, concept.Key, t.Key);
            }

            processes.Add(new JsonObject
            {
                ["key"] = w.StatusFieldKey,
                ["entity"] = concept.Key,
                ["stateField"] = w.StatusFieldKey,
                ["initialState"] = initial.Key,
                ["states"] = states,
                ["transitions"] = transitions,
            });
            ctx.MapProcess(w.Id, concept.Key, w.StatusFieldKey);
        }
        return processes;
    }

    /// <summary>
    /// One command per PLANNED command key, not one per action.
    ///
    /// <para>The definition allows a command to back only one transition, but a user sees one
    /// "Reject" button whichever stage they are on. So an action used once keeps its own key, and an
    /// action used by several transitions produces one command per transition — same label, distinct
    /// keys. The button reads the same; the platform gets what it requires.</para>
    /// </summary>
    private static JsonArray Commands(Context ctx)
    {
        var commands = new JsonArray();
        foreach (var planned in ctx.PlannedCommands)
        {
            var a = planned.Action;
            var command = new JsonObject
            {
                ["key"] = planned.Key,
                ["label"] = a.Label,
                ["entity"] = planned.EntityKey,
                // Empty by design: these commands are bound to process transitions, where the state
                // change IS the effect. The definition allows an empty effect list only in that case.
                ["effects"] = new JsonArray(),
                ["placements"] = Placements(a.AppearsOn),
            };
            if (a.Confirm is { } confirm)
                command["confirm"] = new JsonObject { ["title"] = a.Label, ["message"] = confirm };
            if (a.InputFieldIds.Count > 0)
            {
                var keys = new JsonArray();
                foreach (var fid in a.InputFieldIds)
                    if (ctx.KeyOf(fid) is { } key) keys.Add(key);
                if (keys.Count > 0) command["input"] = new JsonObject { ["fields"] = keys };
            }
            commands.Add(command);
            ctx.MapCommand(planned.Id, planned.EntityKey, planned.Key);
        }
        return commands;
    }

    private static JsonArray Placements(string appearsOn) => appearsOn switch
    {
        ActionPlacements.Row => new JsonArray("tableRow"),
        ActionPlacements.Both => new JsonArray("recordHeader", "tableRow"),
        _ => new JsonArray("recordHeader"),
    };

    // ---- security ---------------------------------------------------------------------------

    /// <summary>Actors lower to roles with real grants. An actor whose responsibilities were prose
    /// only would produce an app where every role can do everything, and every multi-role scenario
    /// would pass for the wrong reason.</summary>
    private static JsonArray Roles(Context ctx)
    {
        var roles = new JsonArray();
        foreach (var a in ctx.Bp.Actors)
        {
            var grants = new JsonArray();
            foreach (var p in a.EntityPermissions)
            {
                var concept = ctx.Bp.Concept(p.ConceptId);
                if (concept is null || !ConceptKinds.ProducesEntity(concept.Kind)) continue;

                var grant = new JsonObject
                {
                    ["entity"] = concept.Key,
                    ["create"] = p.Create,
                    ["read"] = p.Read,
                    ["update"] = p.Update,
                    ["delete"] = p.Delete,
                };

                // Commands are deny-by-default in the definition, so a role that may run one has to
                // be granted it explicitly, on the grant for that command's own entity.
                var commandKeys = new JsonArray();
                foreach (var key in a.ActionPermissions
                             .SelectMany(ctx.CommandKeysForAction)
                             .Where(k => k.EntityKey == concept.Key)
                             .Select(k => k.Key)
                             .Distinct())
                    commandKeys.Add(key);
                if (commandKeys.Count > 0) grant["commands"] = commandKeys;

                var overrides = new JsonArray();
                foreach (var fp in a.FieldPermissions)
                {
                    if (ctx.Bp.Field(fp.FieldId) is not { } f || f.ConceptId != concept.Id) continue;
                    var over = new JsonObject { ["field"] = f.Key };
                    if (!fp.Visible) over["read"] = false;
                    if (!fp.Editable) over["update"] = false;
                    // A permission that changes nothing is noise in a security document.
                    if (over.Count > 1) overrides.Add(over);
                }
                if (overrides.Count > 0) grant["fieldOverrides"] = overrides;

                grants.Add(grant);
            }

            roles.Add(new JsonObject
            {
                ["key"] = a.Key,
                ["name"] = a.Name,
                ["grants"] = grants,
            });
            ctx.MapRole(a.Id, a.Key);
        }
        return roles;
    }

    // ---- experience -------------------------------------------------------------------------

    private static (JsonArray Views, JsonArray Pages) Experience(Context ctx)
    {
        var views = new JsonArray();
        var pages = new JsonArray();
        if (ctx.Bp.Experience is not { } x) return (views, pages);

        foreach (var s in x.Surfaces)
        {
            // A detail surface is the record layout, lowered onto its entity rather than into a
            // standalone view.
            if (s.Surface == SurfaceKinds.Detail) continue;
            if (LowerView(ctx, s) is { } view)
            {
                views.Add(view);
                ctx.MapView(s.Id, s.Key);
            }
        }

        // Home first, settings last: the shell reads page order as navigation order.
        var ordered = x.Pages.Where(p => p.Role == PageRoles.Home)
            .Concat(x.Pages.Where(p => p.Role == PageRoles.Workspace))
            .Concat(x.Pages.Where(p => p.Role == PageRoles.Settings));

        foreach (var p in ordered)
        {
            var blocks = new JsonArray();
            foreach (var sid in p.SurfaceIds)
            {
                var surface = x.Surface(sid);
                if (surface is null || surface.Surface == SurfaceKinds.Detail) continue;

                // A metric on a NON-dashboard surface is a headline above the list — "approved and
                // not yet paid, with a total". The dashboard's own metrics are already inside its
                // view config, so only the other surfaces need a widgets block of their own.
                if (surface.Surface != SurfaceKinds.Dashboard && surface.Metrics.Count > 0)
                {
                    var widgets = new JsonArray();
                    foreach (var m in surface.Metrics)
                        if (LowerMetric(ctx, surface, m) is { } widget) widgets.Add(widget);
                    if (widgets.Count > 0)
                        blocks.Add(new JsonObject { ["kind"] = "widgets", ["widgets"] = widgets });
                }

                blocks.Add(new JsonObject { ["kind"] = "view", ["view"] = surface.Key });
            }
            if (blocks.Count == 0)
            {
                ctx.Fail(p.Id, SpecLayers.Experience, $"page '{p.Label}' would render nothing");
                continue;
            }

            var page = new JsonObject { ["key"] = p.Key, ["label"] = p.Label, ["blocks"] = blocks };
            if (!string.IsNullOrWhiteSpace(p.Icon)) page["icon"] = p.Icon;
            pages.Add(page);
            ctx.MapPage(p.Id, p.Key);
        }

        return (views, pages);
    }

    private static JsonObject? LowerView(Context ctx, SurfaceSpec s)
    {
        var concept = ctx.Bp.Concept(s.ConceptId);
        if (concept is null)
        {
            ctx.Fail(s.Id, SpecLayers.Experience, $"'{s.Label}' is over a concept that does not exist");
            return null;
        }

        var type = s.Surface switch
        {
            SurfaceKinds.Board => "kanban",
            SurfaceKinds.Calendar => "calendar",
            SurfaceKinds.Dashboard => "dashboard",
            _ => "table",
        };

        var view = new JsonObject
        {
            ["key"] = s.Key,
            ["label"] = s.Label,
            ["type"] = type,
            ["entity"] = concept.Key,
        };

        if (Filters(ctx, s.Filters, s.Id, SpecLayers.Experience) is { Count: > 0 } filters)
            view["filters"] = filters;

        if (s.Sort.Count > 0)
        {
            var sort = new JsonArray();
            foreach (var so in s.Sort)
                if (ctx.KeyOf(so.FieldId) is { } key)
                    sort.Add(new JsonObject { ["field"] = key, ["direction"] = so.Direction });
            if (sort.Count > 0) view["sort"] = sort;
        }

        if (ViewConfig(ctx, s, type) is { } config) view["config"] = config;
        return view;
    }

    private static JsonObject? ViewConfig(Context ctx, SurfaceSpec s, string type)
    {
        var columns = new JsonArray();
        foreach (var col in s.Columns)
            if (ctx.KeyOf(col) is { } key) columns.Add(key);

        switch (type)
        {
            case "table":
            {
                var config = new JsonObject { ["columns"] = columns };
                // The blueprint says "filterable"; the definition splits that into facet chips over
                // closed sets and a text search box. Partitioning by field type is the whole rule.
                var facets = new JsonArray();
                var search = new JsonArray();
                foreach (var fid in s.FilterableFieldIds)
                {
                    if (ctx.KeyOf(fid) is not { } key) continue;
                    var fieldType = ctx.Bp.ResolveFieldTarget(fid).Type;
                    if (BlueprintGate.IsFacet(fieldType)) facets.Add(key);
                    else search.Add(key);
                }
                if (facets.Count > 0 || search.Count > 0)
                {
                    var bar = new JsonObject();
                    if (search.Count > 0) bar["search"] = search;
                    if (facets.Count > 0) bar["facets"] = facets;
                    config["filterBar"] = bar;
                }
                return config;
            }
            case "kanban":
            {
                if (ctx.KeyOf(s.GroupByFieldId) is not { } groupBy)
                {
                    ctx.Fail(s.Id, SpecLayers.Experience, $"board '{s.Label}' has nothing to make columns from");
                    return null;
                }
                var config = new JsonObject { ["groupByField"] = groupBy };
                if (columns.Count > 0) config["cardFields"] = columns;
                return config;
            }
            case "calendar":
            {
                if (ctx.KeyOf(s.DateStartFieldId) is not { } dateField)
                {
                    ctx.Fail(s.Id, SpecLayers.Experience, $"calendar '{s.Label}' has no date to place records on");
                    return null;
                }
                var config = new JsonObject { ["dateField"] = dateField };
                var concept = ctx.Bp.Concept(s.ConceptId);
                if (concept?.RecordLabel is { } label && ctx.Bp.Field(label.FieldId) is { } labelField)
                    config["titleField"] = labelField.Key;
                return config;
            }
            case "dashboard":
            {
                var widgets = new JsonArray();
                foreach (var m in s.Metrics)
                    if (LowerMetric(ctx, s, m) is { } widget) widgets.Add(widget);
                if (widgets.Count == 0)
                {
                    ctx.Fail(s.Id, SpecLayers.Experience, $"dashboard '{s.Label}' would show no numbers");
                    return null;
                }
                return new JsonObject { ["widgets"] = widgets };
            }
            default:
                return null;
        }
    }

    /// <summary>A metric with a grouping is a chart; without one it is a single headline number.
    /// That is the whole rule — no judgement, no model.</summary>
    private static JsonObject? LowerMetric(Context ctx, SurfaceSpec s, MetricSpec m)
    {
        var over = ctx.Bp.Concept(m.ConceptId);
        if (over is null)
        {
            ctx.Fail(m.Id, SpecLayers.Experience, $"'{m.Label}' counts a concept that does not exist");
            return null;
        }

        var aggregate = new JsonObject { ["op"] = m.Aggregate };
        if (m.FieldId is { } fid)
        {
            if (ctx.KeyOf(fid) is not { } key)
            {
                ctx.Fail(m.Id, SpecLayers.Experience, $"'{m.Label}' totals a field that does not exist");
                return null;
            }
            aggregate["field"] = key;
        }

        string? groupBy = null;
        if (m.GroupByFieldId is { } gid)
        {
            groupBy = ctx.KeyOf(gid);
            if (groupBy is null)
            {
                ctx.Fail(m.Id, SpecLayers.Experience, $"'{m.Label}' groups by a field that does not exist");
                return null;
            }
            aggregate["groupBy"] = groupBy;
        }

        var source = new JsonObject { ["entity"] = over.Key, ["aggregate"] = aggregate };
        if (Filters(ctx, m.Filters, m.Id, SpecLayers.Experience) is { Count: > 0 } filters)
            source["filters"] = filters;

        return groupBy is null
            ? new JsonObject
            {
                ["type"] = "metric",
                ["source"] = source,
                ["viz"] = new JsonObject { ["title"] = m.Label },
            }
            : new JsonObject
            {
                ["type"] = "chart",
                ["source"] = source,
                ["viz"] = new JsonObject { ["chartType"] = "bar", ["title"] = m.Label },
            };
    }

    // ---- filters ----------------------------------------------------------------------------

    private static JsonArray Filters(Context ctx, IReadOnlyList<Filter> filters, string elementId, string layer)
    {
        var result = new JsonArray();
        foreach (var f in filters)
            if (LowerFilter(ctx, f, elementId, layer) is { } lowered) result.Add(lowered);
        return result;
    }

    private static JsonObject? LowerFilter(Context ctx, Filter f, string elementId, string layer)
    {
        if (ctx.KeyOf(f.FieldId) is not { } key)
        {
            ctx.Fail(elementId, layer, "a condition reads a field that does not exist");
            return null;
        }

        var op = f.Operator switch
        {
            FilterOperators.NotIn => "notIn",
            FilterOperators.IsEmpty => "isEmpty",
            FilterOperators.IsNotEmpty => "isNotEmpty",
            // A window "within the next N days" is the inclusive range from today to today+N. The
            // runtime has no within-days operator, but it does have clock tokens and 'between'.
            FilterOperators.WithinDays => "between",
            FilterOperators.OlderThanDays => "lt",
            var other => other,
        };

        var filter = new JsonObject { ["field"] = key, ["operator"] = op };

        if (FilterOperators.IsValueless(f.Operator)) return filter;

        if (f.Value is not { } v)
        {
            ctx.Fail(elementId, layer, $"a condition on '{key}' has nothing to compare against");
            return null;
        }

        if (v.Context is { } context)
        {
            filter["value"] = context switch
            {
                FilterContexts.CurrentActor => "{{actor.id}}",
                FilterContexts.Today => "{{today}}",
                _ => null,
            };
            if (filter["value"] is null)
            {
                ctx.Fail(elementId, layer, $"a condition on '{key}' uses '{context}', which the runtime has no token for");
                return null;
            }
            return filter;
        }

        if (v.Literal is not { } literal)
        {
            ctx.Fail(elementId, layer, $"a condition on '{key}' has nothing to compare against");
            return null;
        }

        if (f.Operator == FilterOperators.WithinDays)
        {
            if (!literal.TryGetInt32(out var days))
            {
                ctx.Fail(elementId, layer, $"a condition on '{key}' needs a whole number of days");
                return null;
            }
            filter["value"] = new JsonArray("{{today}}", $"{{{{today+{days}}}}}");
            return filter;
        }

        if (f.Operator == FilterOperators.OlderThanDays)
        {
            if (!literal.TryGetInt32(out var days))
            {
                ctx.Fail(elementId, layer, $"a condition on '{key}' needs a whole number of days");
                return null;
            }
            filter["value"] = $"{{{{today-{days}d}}}}";
            return filter;
        }

        filter["value"] = JsonNode.Parse(literal.GetRawText());
        return filter;
    }

    // ---- context ----------------------------------------------------------------------------

    /// <summary>Accumulates the identity map and the diagnostics while lowering walks the blueprint,
    /// so no pass has to be repeated to find out what the previous one decided.</summary>
    private sealed class Context(Blueprint bp)
    {
        public Blueprint Bp { get; } = bp;
        public List<LoweringDiagnostic> Diagnostics { get; } = [];

        private readonly List<IdentityEntry> _entities = [];
        private readonly List<IdentityEntry> _fields = [];
        private readonly List<IdentityEntry> _relations = [];
        private readonly List<IdentityEntry> _processes = [];
        private readonly List<IdentityEntry> _states = [];
        private readonly List<IdentityEntry> _transitions = [];
        private readonly List<IdentityEntry> _commands = [];
        private readonly List<IdentityEntry> _views = [];
        private readonly List<IdentityEntry> _pages = [];
        private readonly List<IdentityEntry> _roles = [];

        public void Fail(string elementId, string layer, string message) =>
            Diagnostics.Add(new LoweringDiagnostic(elementId, layer, message));

        public void MapEntity(string id, string key) => _entities.Add(new(id, null, key));
        public void MapField(string id, string entity, string key) => _fields.Add(new(id, entity, key));
        public void MapRelation(string id, string key) => _relations.Add(new(id, null, key));
        public void MapProcess(string id, string entity, string key) => _processes.Add(new(id, entity, key));
        public void MapState(string id, string entity, string key) => _states.Add(new(id, entity, key));
        public void MapTransition(string id, string entity, string key) => _transitions.Add(new(id, entity, key));
        public void MapCommand(string id, string entity, string key) => _commands.Add(new(id, entity, key));
        public void MapView(string id, string key) => _views.Add(new(id, null, key));
        public void MapPage(string id, string key) => _pages.Add(new(id, null, key));
        public void MapRole(string id, string key) => _roles.Add(new(id, null, key));

        // ---- command planning ----------------------------------------------------------------

        /// <summary>One command the definition will carry. <paramref name="Id"/> is the blueprint id
        /// it is attributed to — the action when that action backs a single transition, the
        /// transition when one action backs several.</summary>
        public sealed record PlannedCommand(string Id, string Key, string EntityKey, ActionSpec Action);

        private readonly List<PlannedCommand> _plannedCommands = [];
        private readonly Dictionary<string, string> _commandKeyByTransition = new(StringComparer.Ordinal);

        public IReadOnlyList<PlannedCommand> PlannedCommands => _plannedCommands;

        public string? CommandKeyFor(string transitionId) =>
            _commandKeyByTransition.GetValueOrDefault(transitionId);

        public IEnumerable<PlannedCommand> CommandKeysForAction(string actionId) =>
            _plannedCommands.Where(c => c.Action.Id == actionId);

        /// <summary>Decides every command key up front. The definition permits a command to back only
        /// one transition, so an action reused across stages becomes several commands sharing a
        /// label — the user still sees one button per state, because only one is ever available.</summary>
        public void PlanCommands()
        {
            foreach (var w in Bp.Workflows)
            {
                var concept = Bp.Concept(w.ConceptId);
                if (concept is null) continue;

                foreach (var a in w.Actions)
                {
                    var backed = w.Transitions.Where(t => t.ActionId == a.Id).ToList();
                    if (backed.Count == 0)
                    {
                        // An action that performs no transition would lower to a command with no
                        // effects and nothing to do — the definition rejects that, so say so here
                        // rather than emitting it and letting the gate explain it in its own terms.
                        Fail(a.Id, SpecLayers.Workflows,
                            $"'{a.Label}' is declared but no transition uses it, so it would do nothing");
                        continue;
                    }

                    if (backed.Count == 1)
                    {
                        _plannedCommands.Add(new(a.Id, a.Key, concept.Key, a));
                        _commandKeyByTransition[backed[0].Id] = a.Key;
                        continue;
                    }

                    foreach (var t in backed)
                    {
                        _plannedCommands.Add(new(t.Id, t.Key, concept.Key, a));
                        _commandKeyByTransition[t.Id] = t.Key;
                    }
                }
            }
        }

        /// <summary>The definition key a blueprint value-target lowered to. Works for data fields,
        /// external references and relationship-backed references alike, because all three end up as
        /// a column on a record.</summary>
        public string? KeyOf(string? blueprintId)
        {
            if (blueprintId is null) return null;
            if (Bp.Field(blueprintId) is { } f) return f.Key;
            if (Bp.Reference(blueprintId) is { } r) return r.Key;
            if (Bp.Relationship(blueprintId) is { } rel) return rel.Key;
            return null;
        }

        /// <summary>
        /// A relation's key must not collide with the reference field the same relationship produced,
        /// nor with another relation.
        ///
        /// <para>The blueprint keys a relationship by the FIELD it creates, so a company that owns
        /// both deals and contacts has two relationships keyed <c>company</c>. Naming the relation
        /// after both ends is what makes it unique, and it reads correctly too — this is the
        /// company-to-deal link, not "the company link".</para>
        /// </summary>
        public string RelationKey(RelationshipSpec r)
        {
            var from = Bp.Concept(r.FromConceptId)?.Key ?? "from";
            var to = Bp.Concept(r.ToConceptId)?.Key ?? "to";
            var key = $"{from}_{to}";
            // Two relationships between the same pair (a deal's main contact AND its billing contact)
            // still need telling apart, so the field key breaks the tie.
            return _relationKeys.Add(key) ? key : $"{from}_{r.Key}_{to}";
        }

        private readonly HashSet<string> _relationKeys = new(StringComparer.Ordinal);

        public DefinitionIdentityMap BuildMap() => new()
        {
            Entities = _entities, Fields = _fields, Relations = _relations,
            Processes = _processes, States = _states, Transitions = _transitions,
            Commands = _commands, Views = _views, Pages = _pages, Roles = _roles,
        };
    }
}

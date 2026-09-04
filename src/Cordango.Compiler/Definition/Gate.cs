// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Cordango.Definition;

/// <summary>
/// The "valid App Definition" gate — structural (JSON Schema) + semantic (referential
/// integrity / unique keys). Port of the Python generator/gate.py; the same rules, so the
/// Python examples + tests are the cross-language conformance corpus.
/// </summary>
public static class Gate
{
    // Base fields the runtime provides on every entity (ItemBase). Always valid to
    // reference; must never be declared in an App Definition.
    public static readonly IReadOnlySet<string> BaseFields = new HashSet<string>
    {
        "id", "company_id", "app_id",
        "created_at", "created_by", "updated_at", "updated_by",
        "deleted_at", "record_state",
    };

    public static List<string> StructuralErrors(JsonNode? doc)
    {
        var errors = new List<string>();
        if (doc is null) { errors.Add("STRUCTURAL: document is null"); return errors; }
        var results = Schemas.AppDefinitionSchema.Evaluate(
            doc.Deserialize<JsonElement>(), new EvaluationOptions { OutputFormat = OutputFormat.Hierarchical });
        if (results.IsValid) return errors;
        CollectSchemaErrors(results, RawSchema,
            (loc, msg) => errors.Add($"STRUCTURAL at [{loc}]: {msg}"));
        if (errors.Count == 0) errors.Add("STRUCTURAL: document failed schema validation");
        return errors;
    }

    /// <summary>Validate ANY document against an ARBITRARY schema with the same pruned
    /// hierarchical walk (and enum hints) the App Definition gate uses. Reusable for tool-call
    /// payloads whose schema varies per call (intent, plan, screens): weak models emit corrupted
    /// argument shapes (observed live 2026-07-17: glm-5.2 leaked its arg-streaming markers into a
    /// string value; kimi emitted 6-token degenerate screens), and nothing else checks a forced
    /// tool call against its own input_schema. Returns an empty list when valid.</summary>
    public static List<string> StructuralErrors(JsonNode? doc, JsonNode schemaJson)
    {
        var errors = new List<string>();
        if (doc is null) { errors.Add("STRUCTURAL: document is null"); return errors; }
        // Compile ANONYMOUSLY: a tool schema derived from app-definition.schema.json inherits its
        // `$id`, and building a schema with an already-registered $id throws ("Overwriting
        // registered schemas is not permitted" — killed every domain attempt live 2026-07-17).
        // Internal "#/..." refs are document-local and need no id.
        if (schemaJson is JsonObject o && o.ContainsKey("$id"))
        {
            var anonymous = (JsonObject)o.DeepClone();
            anonymous.Remove("$id");
            schemaJson = anonymous;
        }
        var results = JsonSchema.FromText(schemaJson.ToJsonString()).Evaluate(
            doc.Deserialize<JsonElement>(), new EvaluationOptions { OutputFormat = OutputFormat.Hierarchical });
        if (results.IsValid) return errors;
        CollectSchemaErrors(results, schemaJson,
            (loc, msg) => errors.Add($"STRUCTURAL at [{loc}]: {msg}"));
        if (errors.Count == 0) errors.Add("STRUCTURAL: document failed schema validation");
        return errors;
    }

    /// <summary>Walks a HIERARCHICAL evaluation tree and reports errors ONLY from invalid
    /// branches, pruning at every valid node. This is what keeps phantom errors out of the repair
    /// prompts: a failed `if` probe, or a failed oneOf/anyOf branch whose SIBLING matched, lives
    /// under a node that is itself valid — the old flat-list walk reported them all whenever ANY
    /// real error existed anywhere in the document (observed live 2026-07-13 for `if` — 24 of 27
    /// errors were phantoms — and 2026-07-16 for oneOf: one bad tiles.format made a perfectly
    /// valid effect target:"self" report 'Value is "string" but should be "object"', burying the
    /// one real error under five phantoms). When NO branch of a oneOf matches, the node itself is
    /// invalid, so each branch's specific errors still come through.</summary>
    private static void CollectSchemaErrors(EvaluationResults node, JsonNode rawSchema,
        Action<string, string> report)
    {
        if (node.IsValid) return;
        // A failed `if` is branch selection, not a failure — its then/else sibling carries the outcome.
        var segments = node.EvaluationPath.ToString().Split('/');
        if (segments.Length > 0 && segments[^1] == "if") return;
        if (node.Errors is { Count: > 0 })
            foreach (var kv in node.Errors)
                report(node.InstanceLocation.ToString(),
                    $"{kv.Value}{AllowedValues(kv.Key, node.SchemaLocation, rawSchema)}");
        if (node.Details is { } details)
            foreach (var child in details) CollectSchemaErrors(child, rawSchema, report);
    }

    private static readonly JsonNode RawSchema = JsonNode.Parse(Schemas.AppDefinitionSchemaJson)!;

    /// <summary>For enum failures, list the allowed values — the generic "should match one of the
    /// values specified by the enum" gives the model nothing to correct toward.</summary>
    private static string AllowedValues(string keyword, Uri schemaLocation, JsonNode rawSchema)
    {
        if (keyword != "enum") return "";
        // SchemaLocation's fragment is a ref-resolved JSON pointer into the schema document.
        JsonNode? sub = rawSchema;
        var fragment = Uri.UnescapeDataString(schemaLocation.Fragment.TrimStart('#', '/'));
        if (fragment.Length == 0) return "";
        foreach (var seg in fragment.Split('/'))
        {
            var key = seg.Replace("~1", "/").Replace("~0", "~");
            sub = sub switch
            {
                JsonObject o => o[key],
                JsonArray a when int.TryParse(key, out var i) && i >= 0 && i < a.Count => a[i],
                _ => null,
            };
            if (sub is null) return "";
        }
        if (sub["enum"] is not JsonArray allowed || allowed.Count == 0) return "";
        return $" (allowed: {string.Join(", ", allowed.Select(a => a?.GetValue<string>()))})";
    }

    /// <summary>Full gate: structural first; semantic only if the structure is sound; then the
    /// design layer (component configs vs the ComponentCatalog contract) once the domain is sound.</summary>
    public static List<string> Validate(JsonNode? doc)
    {
        var structural = StructuralErrors(doc);
        if (structural.Count > 0) return structural;
        var semantic = SemanticErrors(doc);
        return semantic.Count > 0 ? semantic : DesignErrors(doc);
    }

    public static List<string> SemanticErrors(JsonNode? doc)
    {
        var errors = new List<string>();
        if (doc is not JsonObject root) return new List<string> { "SEMANTIC: document is not an object" };

        var entities = Arr(root["entities"]);

        // `uses` — the apps this one says it builds on. Checked here and never rewritten: an
        // undeclared reference is reported by the pipeline as a note, because a definition that
        // links to Organizations without naming it is impolite, not wrong, and refusing it would
        // break every app written before the block existed.
        var declaredApps = new HashSet<string>(StringComparer.Ordinal);
        foreach (var un in Arr(root["uses"]))
        {
            if (un is not JsonObject use) continue;
            var app = Str(use, "app");
            if (app is null) continue;                                  // the schema already required it
            if (!declaredApps.Add(app))
                errors.Add($"SEMANTIC: `uses` names '{app}' twice");
            if (app == Str(root, "key"))
                errors.Add($"SEMANTIC: `uses` names this app itself ('{app}') — a dependency is on another app");
            if (app == "platform")
                errors.Add("SEMANTIC: `uses` names 'platform' — the platform directory (person, department, "
                         + "group) is available to every app and is never declared; reference it with "
                         + "targetApp: 'platform' on a field");
            var named = Arr(use["entities"]).Select(e => e?.GetValue<string>()).OfType<string>().ToList();
            if (CoreAppRegistry.Find(app) is { } coreApp)
            {
                // A core app ships with the platform, so what it holds is known right here — the same
                // reason a reference into one is checked without a database.
                foreach (var want in named.Where(w => !coreApp.EntityKeys.Contains(w)))
                    errors.Add($"SEMANTIC: `uses` says '{app}' has an entity '{want}', which it does not "
                             + $"(it has: {string.Join(", ", coreApp.EntityKeys.OrderBy(x => x))})");
            }
            else if (app.StartsWith("core_", StringComparison.Ordinal))
                errors.Add($"SEMANTIC: `uses` names core app '{app}', which does not exist "
                         + $"(the platform provides: {string.Join(", ", CoreAppRegistry.All.Select(c => c.SystemKey).OrderBy(x => x))})");
            // Any other key is another app in the tenant. The gate is a single-document function and
            // cannot see it, exactly as with a reference field's `targetApp`.
        }

        // entity/field lookups + uniqueness
        var entityFields = new Dictionary<string, HashSet<string>>();
        var entityFieldDefs = new Dictionary<string, Dictionary<string, JsonObject>>();
        var statusFieldOf = new Dictionary<string, string>();   // entity key -> its role:'status' field key
        var entityRoleOf = new Dictionary<string, string>();     // entity key -> its archetype role
        var seenEntities = new HashSet<string>();
        foreach (var en in entities)
        {
            if (en is not JsonObject ent) continue;
            var ekey = Str(ent, "key") ?? "";
            if (!seenEntities.Add(ekey)) errors.Add($"SEMANTIC: duplicate entity key '{ekey}'");
            if (Str(ent, "role") is { } erole) entityRoleOf[ekey] = erole;
            var fkeys = new HashSet<string>();
            var fdefs = new Dictionary<string, JsonObject>();
            var statusFields = new List<string>();
            var startFields = new List<string>();
            var dueFields = new List<string>();
            var unconfirmedFields = new List<string>();
            var differsFields = new List<string>();
            var shareFields = new List<string>();
            var tokenFields = new List<string>();
            foreach (var fn in Arr(ent["fields"]))
            {
                if (fn is not JsonObject f) continue;
                var fk = Str(f, "key") ?? "";
                if (!fkeys.Add(fk)) errors.Add($"SEMANTIC: duplicate field key '{fk}' in entity '{ekey}'");
                if (BaseFields.Contains(fk))
                    errors.Add($"SEMANTIC: entity '{ekey}' declares reserved base field '{fk}' (base fields are runtime-provided and must not be declared)");
                // role:'status' marks the single 'select' field that drives process views (board/timeline).
                if (Str(f, "role") == "status")
                {
                    statusFields.Add(fk);
                    if (Str(f, "type") != "select")
                        errors.Add($"SEMANTIC: field '{ekey}.{fk}' has role 'status' but type '{Str(f, "type")}' — role:'status' is only valid on a 'select' field");
                }
                // `unit` is a DISPLAY suffix ('%', 'x') for a bare number. Money already says its unit
                // through `currency`, and a unit on a date or a select would just be a lie appended
                // to a formatted value.
                if (f["unit"] is not null && Str(f, "type") is not ("integer" or "decimal"))
                    errors.Add($"SEMANTIC: field '{ekey}.{fk}' has a 'unit' but is a '{Str(f, "type")}' — "
                             + "unit suffixes belong on integer/decimal fields (money uses 'currency')");
                // role:'start'/'due' mark the entity's semantic dates (late-start/overdue affordances).
                if (Str(f, "role") is { } dateRole && dateRole is "start" or "due")
                {
                    (dateRole == "start" ? startFields : dueFields).Add(fk);
                    if (Str(f, "type") is not ("date" or "datetime"))
                        errors.Add($"SEMANTIC: field '{ekey}.{fk}' has role '{dateRole}' but type '{Str(f, "type")}' — role:'{dateRole}' is only valid on a 'date'/'datetime' field");
                }
                // A `{{…}}` default is the date grammar, not a literal: "this field starts at today".
                // Without it a required date is unfillable by any writer that isn't a human at a form —
                // an intake submission, an import — because there is nobody to type the date.
                if (f["default"] is JsonValue dv && dv.TryGetValue<string>(out var ds)
                    && ExprTokens.Inner(ds) is { } dtok)
                {
                    if (ExprTokens.Describe(dtok) is { } why)
                        errors.Add($"SEMANTIC: field '{ekey}.{fk}' default '{{{{{dtok}}}}}' is {why}");
                    else if (ExprTokens.ActorTokens.Contains(dtok))
                        errors.Add($"SEMANTIC: field '{ekey}.{fk}' defaults to '{{{{{dtok}}}}}' — the actor token belongs "
                                 + "in a filter or condition; a field that should start as the signed-in user is named "
                                 + "'owner'/'requested_by'/'…_by', which the compiler fills automatically");
                    else if (Str(f, "type") is not ("date" or "datetime"))
                        errors.Add($"SEMANTIC: field '{ekey}.{fk}' defaults to '{{{{{dtok}}}}}' but is a "
                                 + $"'{Str(f, "type")}' field — date tokens only default a 'date'/'datetime' field");
                    else if (dtok.StartsWith("now", StringComparison.Ordinal) && Str(f, "type") == "date")
                        errors.Add($"SEMANTIC: field '{ekey}.{fk}' is a 'date' but defaults to '{{{{{dtok}}}}}', which carries "
                                 + "a time — use '{{today}}'");
                }
                // The two system-maintained marks. 'unconfirmed' holds a LIST of field keys the system
                // wrote into fields a person owns; 'differs' holds a MAP of key to the value the system
                // found, for fields a person has already answered. Both are json, and both must stay
                // writable, because the only thing that maintains either is the system write that
                // filled or compared those fields. A readOnly one compiles to a field `ValidateAndCopy`
                // skips for every caller, so the mark would never be set and every machine-written
                // value would silently present itself as checked.
                if (Str(f, "role") is "unconfirmed" or "differs")
                {
                    var mark = Str(f, "role")!;
                    (mark == "unconfirmed" ? unconfirmedFields : differsFields).Add(fk);
                    if (Str(f, "type") != "json")
                        errors.Add($"SEMANTIC: field '{ekey}.{fk}' has role '{mark}' but type '{Str(f, "type")}' — "
                                 + (mark == "unconfirmed"
                                     ? "it holds the LIST of field keys awaiting confirmation"
                                     : "it holds the MAP of field key to the value the system found")
                                 + ", so it must be a 'json' field");
                    if (f["computed"] is not null)
                        errors.Add($"SEMANTIC: field '{ekey}.{fk}' has role '{mark}' and is computed — a computed "
                                 + "field is recalculated on every write, which would erase the mark it exists to carry");
                }
                // The public perimeter's two halves. `publicShare` is the switch that decides whether a
                // record is served to people with no account; `publicToken` is the address it is
                // served at, and the address IS the credential — so it must be generated rather than
                // typed, which is what readOnly says here.
                if (Str(f, "role") == "publicShare")
                {
                    shareFields.Add(fk);
                    if (Str(f, "type") != "boolean")
                        errors.Add($"SEMANTIC: field '{ekey}.{fk}' has role 'publicShare' but type '{Str(f, "type")}' — "
                                 + "publishing is on or off, so it must be a 'boolean' field");
                    if (f["computed"] is not null)
                        errors.Add($"SEMANTIC: field '{ekey}.{fk}' has role 'publicShare' and is computed — whether "
                                 + "something is exposed to the internet is a decision a person makes, not a value "
                                 + "recalculated on every write");
                }
                if (Str(f, "role") == "publicToken")
                {
                    tokenFields.Add(fk);
                    if (Str(f, "type") != "text")
                        errors.Add($"SEMANTIC: field '{ekey}.{fk}' has role 'publicToken' but type '{Str(f, "type")}' — "
                                 + "it holds an opaque address, so it must be a 'text' field");
                    if (f["unique"]?.GetValue<bool>() != true)
                        errors.Add($"SEMANTIC: field '{ekey}.{fk}' has role 'publicToken' but is not unique — two "
                                 + "records answering to one public address is a link that resolves to whichever "
                                 + "was written first");
                    // Nothing here checks that the token is GENERATED rather than typed, because that
                    // is not the author's to get wrong: the compiler stamps the role readOnly with
                    // `auto:"publicToken"`, so the runtime fills it and client input for it is
                    // dropped. Requiring the author to declare readOnly would be requiring them to
                    // restate a guarantee — and `readOnly` is not an authorable property anyway.
                    if (f["default"] is not null || f["initial"] is not null)
                        errors.Add($"SEMANTIC: field '{ekey}.{fk}' has role 'publicToken' and declares a default — "
                                 + "the platform generates this address with real entropy, and a default would "
                                 + "either be overwritten or, worse, be honoured and make the address guessable");
                }
                // treeAggregate: subtree display math is only meaningful on numbers.
                if (Str(f, "treeAggregate") != null && Str(f, "type") is not ("integer" or "decimal" or "money"))
                    errors.Add($"SEMANTIC: field '{ekey}.{fk}' has treeAggregate but type '{Str(f, "type")}' — treeAggregate is only valid on an 'integer'/'decimal'/'money' field");
                fdefs[fk] = f;
            }
            if (statusFields.Count > 1)
                errors.Add($"SEMANTIC: entity '{ekey}' has multiple role:'status' fields ({string.Join(", ", statusFields)}) — at most one is allowed");
            if (statusFields.Count >= 1) statusFieldOf[ekey] = statusFields[0];
            if (startFields.Count > 1)
                errors.Add($"SEMANTIC: entity '{ekey}' has multiple role:'start' fields ({string.Join(", ", startFields)}) — at most one is allowed");
            if (dueFields.Count > 1)
                errors.Add($"SEMANTIC: entity '{ekey}' has multiple role:'due' fields ({string.Join(", ", dueFields)}) — at most one is allowed");
            if (unconfirmedFields.Count > 1)
                errors.Add($"SEMANTIC: entity '{ekey}' has multiple role:'unconfirmed' fields ({string.Join(", ", unconfirmedFields)}) — at most one is allowed");
            if (differsFields.Count > 1)
                errors.Add($"SEMANTIC: entity '{ekey}' has multiple role:'differs' fields ({string.Join(", ", differsFields)}) — at most one is allowed");
            if (shareFields.Count > 1)
                errors.Add($"SEMANTIC: entity '{ekey}' has multiple role:'publicShare' fields ({string.Join(", ", shareFields)}) — at most one is allowed");
            if (tokenFields.Count > 1)
                errors.Add($"SEMANTIC: entity '{ekey}' has multiple role:'publicToken' fields ({string.Join(", ", tokenFields)}) — at most one is allowed");
            // The two are a pair, and either one alone is a mistake rather than a subset: a share flag
            // with no token has no address to be served at, and a token with no flag is an address
            // that can never be turned off. Both directions are errors so neither can be reached by
            // deleting a field and not noticing.
            if (shareFields.Count > 0 && tokenFields.Count == 0)
                errors.Add($"SEMANTIC: entity '{ekey}' has a role:'publicShare' field ('{shareFields[0]}') but no "
                         + "role:'publicToken' field — publishing a record needs a public address to publish it at");
            if (tokenFields.Count > 0 && shareFields.Count == 0)
                errors.Add($"SEMANTIC: entity '{ekey}' has a role:'publicToken' field ('{tokenFields[0]}') but no "
                         + "role:'publicShare' field — an address with no switch is one nobody can turn off");
            entityFields[ekey] = fkeys;
            entityFieldDefs[ekey] = fdefs;
        }

        // The calendar opt-in, resolved by the SAME code AppCompiler will use to stamp the answer
        // into the manifest. One derivation with two callers, deliberately: a checker and a builder
        // that each implement one grammar agree right up until they quietly do not, which is how
        // `{{today+1w}}` once passed both the check and the build and then resolved to a literal string.
        //
        // It runs after the per-entity loop because the `who` may live on the ownedBy PARENT, which is
        // another entity and may not have been walked yet.
        foreach (var en in entities.OfType<JsonObject>())
            errors.AddRange(CalendarResolver.Resolve(entities, en).Errors);

        // A select's allowed values must come from SOMEWHERE. Normally that is its own `options`; the
        // one exception is a field a process governs, where the process's states ARE the values —
        // AppCompiler.CanonicalizeProcesses rewrites the field's options (and default) from them, so
        // authoring both is duplication the compiler discards. JSON Schema cannot see across the
        // document to `processes`, so the rule lives here rather than in the select's `required`.
        var governedField = new HashSet<string>();          // "entity|field"
        foreach (var pn in Arr(root["processes"]))
            if (pn is JsonObject pr && Str(pr, "entity") is { } pe && Str(pr, "stateField") is { } psf)
                governedField.Add($"{pe}|{psf}");
        foreach (var (ekey, fdefs) in entityFieldDefs)
            foreach (var (fk, f) in fdefs)
            {
                if (Str(f, "type") is not ("select" or "multiselect")) continue;
                if (governedField.Contains($"{ekey}|{fk}")) continue;
                if (f["options"] is not JsonArray { Count: > 0 })
                    errors.Add($"SEMANTIC: field '{ekey}.{fk}' is a select with no options — give it 'options', "
                        + "or let a process govern it (a process's states become its options)");
            }

        bool FieldExists(string? entityKey, string? fieldKey) =>
            fieldKey != null && (BaseFields.Contains(fieldKey)
                || (entityKey != null && entityFields.TryGetValue(entityKey, out var fs) && fs.Contains(fieldKey)));

        string? FieldTypeOf(string? e, string? f) =>
            f == null ? null
            : BaseFieldTypes.TryGetValue(f, out var bt) ? bt
            : e != null && entityFieldDefs.TryGetValue(e, out var dm) && dm.TryGetValue(f, out var fdm) ? Str(fdm, "type") : null;

        // ---- behavior layer: processes + commands + the shared effect/condition validators --------
        // Pre-scan processes so commands can be checked against them (a command with empty effects is
        // legal only when a transition binds it), then validate commands, then validate processes deeply.
        var behavior = new BehaviorCtx(seenEntities, FieldExists, FieldTypeOf, entityFieldDefs, statusFieldOf,
            EntityDefs: (doc["entities"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                .Where(e => Str(e, "key") is not null)
                .GroupBy(e => Str(e, "key")!).ToDictionary(g => g.Key, g => g.First()));
        var processByEntity = new Dictionary<string, JsonObject>();
        var stateKeysByEntity = new Dictionary<string, HashSet<string>>();
        var transitionBoundCommands = new HashSet<string>();       // "entity|commandKey"
        foreach (var pn in Arr(root["processes"]))
        {
            if (pn is not JsonObject p || Str(p, "entity") is not { } pe) continue;
            processByEntity.TryAdd(pe, p);
            var sk = stateKeysByEntity.TryGetValue(pe, out var e0) ? e0 : stateKeysByEntity[pe] = new();
            foreach (var sn in Arr(p["states"])) if (Str(sn as JsonObject, "key") is { } skk) sk.Add(skk);
            foreach (var tn in Arr(p["transitions"]))
            {
                var named = Str(tn as JsonObject, "command");
                if (named is not null) { transitionBoundCommands.Add(pe + "|" + named); continue; }

                // A SYNTHESIZED command is transition-bound too, and saying so is what keeps the rest
                // of the gate's rules correct now that it knows such commands exist. In particular
                // ValidateRecordHeaderCommands exempts transition-bound commands from having to
                // appear in a hub's `actions` — the process places them, not the hub — and without
                // this line three corpus apps started failing that rule for buttons nobody authored.
                if (Str(tn as JsonObject, "key") is { } tk)
                    transitionBoundCommands.Add(pe + "|" + ProcessCommands.SynthesizedKey(pe, tk));
            }
        }
        behavior = behavior with { StateKeysByEntity = stateKeysByEntity };
        var commandsByEntity = new Dictionary<string, Dictionary<string, JsonObject>>();
        ValidateCommands(root["commands"], behavior, transitionBoundCommands, commandsByEntity, errors);
        ValidateProcesses(root["processes"], behavior, commandsByEntity, errors);
        ValidateInitialRules(behavior, errors);
        ValidateComputedFields(behavior, errors);

        // references resolve; displayField resolves
        foreach (var en in entities)
        {
            if (en is not JsonObject ent) continue;
            var ekey = Str(ent, "key");
            var disp = Str(ent, "displayField");
            if (disp != null && !FieldExists(ekey, disp))
                errors.Add($"SEMANTIC: entity '{ekey}' displayField '{disp}' is not a field of '{ekey}'");

            // Composite uniques become real database constraints, so a key that does not resolve is
            // not a cosmetic slip — it is a constraint that silently never gets created, and the
            // duplicate it was meant to prevent shows up in production instead.
            foreach (var cn in Arr(ent["unique"]))
            {
                if (cn is not JsonArray combo) continue;
                var keys = combo.Select(k => k?.GetValue<string>()).Where(k => k != null).ToList();
                foreach (var k in keys)
                    if (!FieldExists(ekey, k!))
                        errors.Add($"SEMANTIC: entity '{ekey}' unique combination names '{k}', which is not a field of '{ekey}'");
                if (keys.Count != keys.Distinct().Count())
                    errors.Add($"SEMANTIC: entity '{ekey}' unique combination repeats a field — a field is only unique with itself once");
                // A one-field combination is the FIELD's `unique` flag written in the wrong place.
                // Allowing both spellings means two ways to say one thing and two places to look.
                if (keys.Count == 1)
                    errors.Add($"SEMANTIC: entity '{ekey}' unique combination lists one field ('{keys[0]}') — set `unique: true` on the field instead");
            }
            foreach (var fn in Arr(ent["fields"]))
            {
                if (fn is not JsonObject f) continue;

                // `input` says how a value is ENTERED, and each control only knows how to read and
                // write one storage shape. A timezone picker on a number, or the weekly-hours grid on
                // a text column, degrades to a plain box at render time — silently, and only for
                // whoever happens to open that form.
                if (Str(f, "input") is { } input)
                {
                    var wants = input switch
                    {
                        "timezone" or "slug" => "text",
                        "weeklyHours" => "json",
                        _ => null,
                    };
                    if (wants is not null && Str(f, "type") != wants)
                        errors.Add($"SEMANTIC: field '{ekey}.{Str(f, "key")}' has input '{input}', which needs "
                                 + $"type '{wants}' (it is '{Str(f, "type")}')");
                }

                if (Str(f, "type") != "reference")
                {
                    // optionsFilter narrows which TARGET RECORDS a picker offers, so it only means
                    // anything where there are target records. A select's choices are its `options`.
                    if (f["optionsFilter"] is JsonArray)
                        errors.Add($"SEMANTIC: field '{ekey}.{Str(f, "key")}' has optionsFilter but is not a reference — "
                                 + "optionsFilter narrows which records a picker offers; a select's choices are its `options`.");
                    continue;
                }
                var tgt = Str(f, "targetEntity");
                var tapp = Str(f, "targetApp");
                if (tapp == "platform")
                {
                    if (tgt == null || !Schemas.PlatformEntityKeys.Contains(tgt))
                        errors.Add($"SEMANTIC: field '{ekey}.{Str(f, "key")}' references unknown platform entity '{tgt}' (known: {string.Join(", ", Schemas.PlatformEntityKeys.OrderBy(x => x))})");
                }
                else if (CoreAppRegistry.Find(tapp) is { } core)
                {
                    // A core app ships WITH the platform, so its entity list is known here and a
                    // reference into one is checked like any other — without the gate reaching for a
                    // database and losing its purity. A typo'd targetEntity fails now, not as a blank
                    // column after the app is built.
                    if (tgt == null || !core.EntityKeys.Contains(tgt))
                        errors.Add($"SEMANTIC: field '{ekey}.{Str(f, "key")}' references unknown entity '{tgt}' in core app '{tapp}' (known: {string.Join(", ", core.EntityKeys.OrderBy(x => x))})");
                }
                else if (tapp != null) { /* an arbitrary app key — general cross-app refs are not supported */ }
                else if (tgt == null || !seenEntities.Contains(tgt))
                    errors.Add($"SEMANTIC: field '{ekey}.{Str(f, "key")}' references unknown entity '{tgt}'");

                // 'setNull' on a REQUIRED reference is a rule that cannot be carried out: clearing the
                // field makes the row invalid, so the delete would either fail or leave the reference
                // dangling — the exact thing the rule was chosen to prevent. Cascade or restrict.
                // Only local references: a cross-app rule is a statement of intent this store cannot
                // act on either way, and the convention predates the runtime honouring any of this.
                if (Str(f, "onDelete") == "setNull" && tapp == null
                    && f["required"]?.GetValue<bool>() == true)
                    errors.Add($"SEMANTIC: field '{ekey}.{Str(f, "key")}' is required, so onDelete "
                             + "'setNull' cannot run — clearing it would leave an invalid row. "
                             + "Use 'cascade' (the row goes with its parent) or 'restrict' (the parent "
                             + "cannot be deleted while it has any).");

                // optionsFilter: a leaf naming a field the TARGET does not have filters on a value
                // that is never there, so the picker comes back empty — and an empty picker reads as
                // "nobody has one yet", which is indistinguishable from "this rule is wrong".
                if (f["optionsFilter"] is JsonArray optionsFilter)
                {
                    var fkey = Str(f, "key");
                    // Only a LOCAL target's fields are knowable here. A platform or core target is
                    // resolved at render time, and guessing at its shape would fail valid apps.
                    var localTarget = tapp == null && tgt != null && seenEntities.Contains(tgt);
                    foreach (var ln in optionsFilter)
                    {
                        if (ln is not JsonObject leaf) continue;
                        if (Str(leaf, "path") is { Length: > 0 } hop)
                            errors.Add($"SEMANTIC: field '{ekey}.{fkey}' optionsFilter uses path '{hop}' — a picker "
                                     + "narrows on the target's OWN fields; use 'field'.");
                        else if (Str(leaf, "field") is { } lf && localTarget && !FieldExists(tgt, lf))
                            errors.Add($"SEMANTIC: field '{ekey}.{fkey}' optionsFilter names '{lf}', which is not a "
                                     + $"field of '{tgt}' — the picker would offer nothing and read as empty rather than wrong.");
                    }
                }
            }
        }

        // entity subordination (ownedBy): parent resolves (and isn't self); 'via' is a local reference on
        // this entity that points to the parent. Marks the entity as embedded-in-parent (no top-level nav).
        foreach (var en in entities)
        {
            if (en is not JsonObject ent || ent["ownedBy"] is not JsonObject owned) continue;
            var ekey = Str(ent, "key");
            var parent = Str(owned, "parent");
            var via = Str(owned, "via");
            if (parent == null || !seenEntities.Contains(parent))
                errors.Add($"SEMANTIC: entity '{ekey}' ownedBy.parent '{parent}' is unknown");
            else if (parent == ekey)
                errors.Add($"SEMANTIC: entity '{ekey}' cannot be ownedBy itself");
            if (via == null || !FieldExists(ekey, via))
                errors.Add($"SEMANTIC: entity '{ekey}' ownedBy.via '{via}' is not a field of '{ekey}'");
            else if (entityFieldDefs.TryGetValue(ekey ?? "", out var ofs) && ofs.TryGetValue(via, out var vf))
            {
                // Subordination means "these rows are PART OF that parent record" — the renderer nests
                // them, the parent's detail owns them, deleting the parent takes them with it. None of
                // that survives the field pointing into a different app's table, so a cross-app `via`
                // is rejected outright rather than silently producing an entity with no way in.
                if (Str(vf, "targetApp") is { } vapp)
                    errors.Add($"SEMANTIC: entity '{ekey}' ownedBy.via '{via}' points into '{vapp}' — an entity can only be owned by a parent in the same app");
                else if (Str(vf, "type") != "reference" || (parent != null && Str(vf, "targetEntity") != parent))
                    errors.Add($"SEMANTIC: entity '{ekey}' ownedBy.via '{via}' must be a reference to the parent '{parent}'");
            }
        }

        // relations
        var seenRel = new HashSet<string>();
        foreach (var rn in Arr(root["relations"]))
        {
            if (rn is not JsonObject rel) continue;
            var rkey = Str(rel, "key") ?? "";
            if (!seenRel.Add(rkey)) errors.Add($"SEMANTIC: duplicate relation key '{rkey}'");
            var frm = Str(rel, "fromEntity");
            var to = Str(rel, "toEntity");
            // Relation endpoints may be app entities OR platform entities (person/department/group): an app
            // legitimately relates to platform people (the FK lives on the app entity's reference field).
            if (frm == null || !(seenEntities.Contains(frm) || Schemas.PlatformEntityKeys.Contains(frm)))
                errors.Add($"SEMANTIC: relation '{rkey}' fromEntity '{frm}' is unknown");
            if (to == null || !(seenEntities.Contains(to) || Schemas.PlatformEntityKeys.Contains(to)))
                errors.Add($"SEMANTIC: relation '{rkey}' toEntity '{to}' is unknown");
            var inv = Str(rel, "inverseField");
            if (inv != null && to != null && entityFieldDefs.TryGetValue(to, out var tf))
            {
                if (!tf.TryGetValue(inv, out var fdef))
                    errors.Add($"SEMANTIC: relation '{rkey}' inverseField '{inv}' is not a field of '{to}'");
                else if (Str(fdef, "type") != "reference" || Str(fdef, "targetEntity") != frm)
                    errors.Add($"SEMANTIC: relation '{rkey}' inverseField '{to}.{inv}' must be a reference to '{frm}'");
            }
        }

        // views
        var seenView = new HashSet<string>();
        var viewKeys = new HashSet<string>();
        foreach (var vn in Arr(root["views"]))
        {
            if (vn is not JsonObject v) continue;
            var vkey = Str(v, "key") ?? "";
            if (!seenView.Add(vkey)) errors.Add($"SEMANTIC: duplicate view key '{vkey}'");
            viewKeys.Add(vkey);
            var vent = Str(v, "entity");
            if (vent == null || !seenEntities.Contains(vent)) { errors.Add($"SEMANTIC: view '{vkey}' entity '{vent}' is unknown"); continue; }
            foreach (var (clause, label) in new[] { (v["filters"], "filter"), (v["sort"], "sort") })
                foreach (var cn in Arr(clause))
                    if (cn is JsonObject c && !FieldExists(vent, Str(c, "field")))
                        errors.Add($"SEMANTIC: view '{vkey}' {label} references unknown field '{Str(c, "field")}' on entity '{vent}'");

            // Component preconditions (catalog-driven): the view type's required data shape must exist
            // on its entity — e.g. kanban needs a role:'status' field, calendar/timeline need a date field.
            var vtype = Str(v, "type");
            var ef = entityFieldDefs.TryGetValue(vent, out var m) ? m : new Dictionary<string, JsonObject>();
            if (vtype != null && ComponentCatalog.Find("view." + vtype) is { Requires.DataShape: { } shapes })
            {
                foreach (var d in shapes)
                {
                    var ok = (d.Role is null || ef.Values.Any(f => Str(f, "role") == d.Role))
                          && (d.FieldTypes is null || ef.Values.Any(f => d.FieldTypes!.Contains(Str(f, "type"))));
                    if (!ok)
                        errors.Add($"SEMANTIC: view '{vkey}' ({vtype}) requires {d.Description} on entity '{vent}', which has none");
                }
            }
            // A board groups by ANY field with discrete values: a process status (drag = transition) OR a
            // plain select/reference like station/office/owner (drag = reassign). Pinning this to
            // role:'status' ruled out assignment boards — the same control, just grouped differently.
            if (vtype == "kanban" && v["config"] is JsonObject vcfg && Str(vcfg, "groupByField") is { } gbf)
            {
                if (!ef.TryGetValue(gbf, out var gbfDef))
                    errors.Add($"SEMANTIC: view '{vkey}' kanban groupByField '{gbf}' is not a field of '{vent}'");
                else if (Str(gbfDef, "type") is { } gt && gt is not ("select" or "reference"))
                    errors.Add($"SEMANTIC: view '{vkey}' kanban groupByField '{gbf}' must be a select or reference field " +
                               $"(is '{gt}') — a board needs discrete columns");
            }
        }

        // workflows — trigger resolves; the optional `when` guard and the `effects` list are validated
        // by the shared condition/effect validators (same language commands use).
        var seenWf = new HashSet<string>();
        foreach (var wn in Arr(root["workflows"]))
        {
            if (wn is not JsonObject wf) continue;
            var wkey = Str(wf, "key") ?? "";
            if (!seenWf.Add(wkey)) errors.Add($"SEMANTIC: duplicate workflow key '{wkey}'");
            var trigger = wf["trigger"] as JsonObject;
            var ctx = Str(trigger, "entity");
            if (ctx != null && !seenEntities.Contains(ctx)) errors.Add($"SEMANTIC: workflow '{wkey}' trigger entity '{ctx}' is unknown");
            var where = $"workflow '{wkey}'";
            if (wf["when"] is JsonObject wfWhen)
            {
                if (ctx == null) errors.Add($"SEMANTIC: {where} has a 'when' guard but its trigger has no entity to resolve it against");
                else ValidateCondition(wfWhen, ctx, where, behavior, errors);
            }
            if (ctx != null && seenEntities.Contains(ctx))
                ValidateEffects(wf["effects"], ctx, where, behavior, errors, Str(trigger, "event"));
        }

        // roles — grants resolve; command grants must name commands on the grant's entity (G1/G2)
        var seenRole = new HashSet<string>();
        foreach (var rn in Arr(root["roles"]))
        {
            if (rn is not JsonObject role) continue;
            var rk = Str(role, "key") ?? "";
            if (!seenRole.Add(rk)) errors.Add($"SEMANTIC: duplicate role key '{rk}'");
            foreach (var gn in Arr(role["grants"]))
            {
                if (gn is not JsonObject g) continue;
                var gent = Str(g, "entity");
                if (gent != "*" && (gent == null || !seenEntities.Contains(gent)))
                    errors.Add($"SEMANTIC: role '{rk}' grants on unknown entity '{gent}'");
                if (gent != "*")
                    foreach (var fon in Arr(g["fieldOverrides"]))
                        if (fon is JsonObject fo && !FieldExists(gent, Str(fo, "field")))
                            errors.Add($"SEMANTIC: role '{rk}' fieldOverride references unknown field '{Str(fo, "field")}' on entity '{gent}'");
                foreach (var cn in Arr(g["commands"]))
                {
                    if (cn?.GetValue<string>() is not { } ck) continue;
                    if (gent == "*")
                    {
                        if (ck != "*")
                            errors.Add($"SEMANTIC: role '{rk}' wildcard-entity grant may only grant commands ['*'], not '{ck}' — grant named commands on a specific-entity grant");
                    }
                    else if (ck != "*" && !(commandsByEntity.TryGetValue(gent ?? "", out var cmds) && cmds.ContainsKey(ck)))
                        errors.Add($"SEMANTIC: role '{rk}' grants unknown command '{ck}' on entity '{gent}'");
                }
            }
        }

        // Block trees: pages render with COLLECTION binding, an entity's authored detail with
        // RECORD binding — each binding has its own legal block kinds (see ValidateBlocks).
        // Page key → entity, so a KPI's deep link can be checked against its target page.
        var pageEntityOf = new Dictionary<string, string?>();
        foreach (var pn in Arr(root["pages"]))
            if (pn is JsonObject pg && Str(pg, "key") is { } pk) pageEntityOf.TryAdd(pk, Str(pg, "entity"));
        var entityKindOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var en0 in entities.OfType<JsonObject>())
            if (Str(en0, "key") is { } ek0) entityKindOf[ek0] = Str(en0, "kind") ?? "collection";
        var blockCtx = new BlockCtx(seenEntities, viewKeys, FieldExists, FieldTypeOf, entityFieldDefs, commandsByEntity, pageEntityOf, entityRoleOf, entityKindOf);
        foreach (var pn in Arr(root["pages"]))
            if (pn is JsonObject page) ValidatePage(page, blockCtx, errors);
        foreach (var en in entities)
        {
            if (en is not JsonObject ent) continue;
            var ekey = Str(ent, "key") ?? "";
            if (ent["detail"] is JsonObject det)
            {
                ValidateBlocks(det["blocks"], $"entity '{ekey}' detail", "record", ekey, blockCtx, errors);
                ValidatePairedDateAxes(det["blocks"], $"entity '{ekey}' detail", errors);
            }
            if (ent["form"] is JsonObject form)
                ValidateFormBlocks(form["blocks"], $"entity '{ekey}' form", ekey, blockCtx, errors);
        }

        // A `recordHeader` command must actually be listed in its entity's hub actions.
        //
        // The hub renders exactly the keys in `actions` (defaulting to edit+delete) — `placements` does
        // not add anything to it. So a command declaring `recordHeader` and left out of `actions` is a
        // button that never draws, with nothing anywhere saying so: the command exists, the role grants
        // it, the API runs it, and the only surface a user could reach it from is silently missing.
        // Checked here rather than in ValidateCommands because it needs the entity's block tree.
        ValidateRecordHeaderCommands(entities, commandsByEntity, transitionBoundCommands, errors);

        // Forms archetype coherence — only when the app enables the 'forms' module (see architecture-app-archetypes.md).
        if (Arr(root["plugins"]).OfType<JsonObject>().Any(p => Str(p, "id") == "forms"))
            ValidateFormsArchetype(entities, entityFieldDefs, errors);

        return errors;
    }

    /// <summary>One page: its entity resolves, its navSource is coherent, and its block tree is legal
    /// under COLLECTION binding. Shared by the whole-document gate and by <see cref="PageErrors"/>, so
    /// a personally-authored page is held to exactly the rules an app-authored one is.</summary>
    private static void ValidatePage(JsonObject page, BlockCtx ctx, List<string> errors)
    {
        var pkey = Str(page, "key") ?? "";
        var pageEntity = Str(page, "entity");
        if (pageEntity is { } pent && !ctx.Entities.Contains(pent))
            errors.Add($"SEMANTIC: page '{pkey}' entity '{pent}' is unknown");
        // navSource turns the page into records-as-nav: it needs a resolvable entity, and its
        // labelField/sort must be fields of that entity (the shell reads them to label + order entries).
        if (page["navSource"] is JsonObject navSrc)
        {
            if (pageEntity is null || !ctx.Entities.Contains(pageEntity))
                errors.Add($"SEMANTIC: page '{pkey}' navSource requires the page's 'entity' to be a known entity");
            else
                foreach (var (prop, req) in new[] { ("labelField", true), ("sort", false) })
                    if (Str(navSrc, prop) is { } nf && !ctx.FieldExists(pageEntity, nf))
                        errors.Add($"SEMANTIC: page '{pkey}' navSource {prop} '{nf}' is not a field of '{pageEntity}'");
                    else if (req && Str(navSrc, prop) is null)
                        errors.Add($"SEMANTIC: page '{pkey}' navSource needs a '{prop}'");
        }
        ValidateBlocks(page["blocks"], $"page '{pkey}'", "collection", null, ctx, errors);
        ValidatePairedDateAxes(page["blocks"], $"page '{pkey}'", errors);
    }

    /// <summary>
    /// Validate ONE page against a COMPILED MANIFEST's roster of entities, views and commands.
    ///
    /// <para>This is the gate for a personal view — a page a user authors over an app they do not own.
    /// It reuses <see cref="ValidateBlocks"/> rather than forking it, so every block rule the app
    /// gate enforces applies here too, and keeps applying as new block kinds land.</para>
    ///
    /// <para><b>Pass the caller's permission-RESTRICTED manifest</b>, not the raw one: the roster IS
    /// the authorization boundary, so an entity or field the caller may not read simply does not
    /// exist as far as this validation is concerned, and a page naming it cannot be saved.</para>
    ///
    /// <para>Structure (the page's own shape) is checked separately via
    /// <see cref="StructuralErrors(JsonNode?, JsonNode)"/> against <c>#/$defs/page</c> — a compiled
    /// manifest is NOT a valid app definition (the compiler adds <c>system</c>/<c>auto</c>/<c>detail</c>,
    /// which the schema's closed shapes reject), so the whole-document gate cannot be used here.</para>
    /// </summary>
    public static List<string> PageErrors(JsonObject? manifest, JsonObject? page)
    {
        var errors = new List<string>();
        if (manifest is null) { errors.Add("SEMANTIC: no app manifest to validate against"); return errors; }
        if (page is null) { errors.Add("SEMANTIC: the view has no page"); return errors; }
        ValidatePage(page, BlockCtxFrom(manifest), errors);
        return errors;
    }

    /// <summary>
    /// Validate a saved VIEW PRESET — a personal filter/sort/column set over one of the app's own
    /// views — against a compiled manifest.
    ///
    /// <para>Everything resolves against the BASE VIEW'S entity, because that is what a preset
    /// refines. It reuses the same filter/sort/groupBy validators the app gate uses on a view, so a
    /// preset's filter is held to the same rules as an authored one; a preset is a narrower thing
    /// than a page, not a laxer one.</para>
    ///
    /// <para>As with <see cref="PageErrors"/>, pass the caller's permission-RESTRICTED manifest: a
    /// base view whose entity the caller may not read is simply absent from it, so the preset cannot
    /// be authored at all.</para>
    /// </summary>
    public static List<string> PresetErrors(JsonObject? manifest, string? baseViewKey, JsonObject? preset)
    {
        var errors = new List<string>();
        if (manifest is null) { errors.Add("SEMANTIC: no app manifest to validate against"); return errors; }
        if (preset is null) { errors.Add("SEMANTIC: the preset has no settings"); return errors; }

        var view = Arr(manifest["views"]).OfType<JsonObject>()
            .FirstOrDefault(v => Str(v, "key") == baseViewKey);
        if (view is null)
        {
            errors.Add($"SEMANTIC: preset refers to unknown view '{baseViewKey}'");
            return errors;
        }
        var entity = Str(view, "entity");
        var ctx = BlockCtxFrom(manifest);
        if (entity is null || !ctx.Entities.Contains(entity))
        {
            errors.Add($"SEMANTIC: view '{baseViewKey}' has no entity to filter");
            return errors;
        }

        var where = $"preset on view '{baseViewKey}'";
        foreach (var f in Arr(preset["filters"]).OfType<JsonObject>())
            ValidateFilterAddress(f, entity, where, ctx, errors);
        foreach (var s in Arr(preset["sort"]).OfType<JsonObject>())
            if (!ctx.FieldExists(entity, Str(s, "field")))
                errors.Add($"SEMANTIC: {where}: sort field '{Str(s, "field")}' is not a field of '{entity}'");
        foreach (var c in Arr(preset["columns"]))
            if (c?.GetValue<string>() is { } ck && !ctx.FieldExists(entity, ck))
                errors.Add($"SEMANTIC: {where}: column '{ck}' is not a field of '{entity}'");
        ValidateGroupBy(preset["groupBy"] as JsonObject, entity, where, ctx, errors);
        return errors;
    }

    /// <summary>The block-validation roster read straight off a compiled manifest. Deliberately does
    /// NOT re-validate the manifest itself — it is the output of a build that already passed the gate;
    /// anything wrong with it is a compiler bug, not this caller's problem.</summary>
    private static BlockCtx BlockCtxFrom(JsonObject manifest)
    {
        var entityFields = new Dictionary<string, HashSet<string>>();
        var entityFieldDefs = new Dictionary<string, Dictionary<string, JsonObject>>();
        var entityRoleOf = new Dictionary<string, string>();
        var seenEntities = new HashSet<string>();
        foreach (var en in Arr(manifest["entities"]).OfType<JsonObject>())
        {
            if (Str(en, "key") is not { } ekey) continue;
            seenEntities.Add(ekey);
            if (Str(en, "role") is { } role) entityRoleOf[ekey] = role;
            var fkeys = new HashSet<string>();
            var fdefs = new Dictionary<string, JsonObject>();
            foreach (var f in Arr(en["fields"]).OfType<JsonObject>())
                if (Str(f, "key") is { } fk) { fkeys.Add(fk); fdefs[fk] = f; }
            entityFields[ekey] = fkeys;
            entityFieldDefs[ekey] = fdefs;
        }

        bool FieldExists(string? entityKey, string? fieldKey) =>
            fieldKey != null && (BaseFields.Contains(fieldKey)
                || (entityKey != null && entityFields.TryGetValue(entityKey, out var fs) && fs.Contains(fieldKey)));
        string? FieldTypeOf(string? e, string? f) =>
            f == null ? null
            : BaseFieldTypes.TryGetValue(f, out var bt) ? bt
            : e != null && entityFieldDefs.TryGetValue(e, out var dm) && dm.TryGetValue(f, out var fd) ? Str(fd, "type") : null;

        var viewKeys = new HashSet<string>();
        foreach (var v in Arr(manifest["views"]).OfType<JsonObject>())
            if (Str(v, "key") is { } vk) viewKeys.Add(vk);

        var commandsByEntity = new Dictionary<string, Dictionary<string, JsonObject>>();
        foreach (var c in Arr(manifest["commands"]).OfType<JsonObject>())
            if (Str(c, "entity") is { } ce && Str(c, "key") is { } ck)
                (commandsByEntity.TryGetValue(ce, out var m) ? m : commandsByEntity[ce] = new())[ck] = c;

        // The app's own pages are legal deep-link targets. A personal view linking to ANOTHER personal
        // view is not supported — that would make one user's page depend on another's, which is a
        // dependency nothing can enforce when the target is deleted.
        var pageEntityOf = new Dictionary<string, string?>();
        foreach (var p in Arr(manifest["pages"]).OfType<JsonObject>())
            if (Str(p, "key") is { } pk) pageEntityOf.TryAdd(pk, Str(p, "entity"));

        var entityKindOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e0 in Arr(manifest["entities"]).OfType<JsonObject>())
            if (Str(e0, "key") is { } ek0) entityKindOf[ek0] = Str(e0, "kind") ?? "collection";

        return new BlockCtx(seenEntities, viewKeys, FieldExists, FieldTypeOf, entityFieldDefs,
            commandsByEntity, pageEntityOf, entityRoleOf, entityKindOf);
    }

    // Roles: formTemplate (survey) -> formField (questions, ref the template + carry an 'answerType') ->
    // formResponse (submission, refs the template) -> formAnswer (refs response + field, holds 'answerValue').
    private static void ValidateFormsArchetype(
        JsonArray entities, Dictionary<string, Dictionary<string, JsonObject>> fieldDefs, List<string> errors)
    {
        var role = new Dictionary<string, string>();               // entity key -> role
        var byRole = new Dictionary<string, List<string>>();
        foreach (var en in entities)
        {
            if (en is not JsonObject ent) continue;
            if (Str(ent, "key") is not { } key || Str(ent, "role") is not { } r) continue;
            role[key] = r;
            (byRole.TryGetValue(r, out var l) ? l : byRole[r] = new()).Add(key);
        }

        bool RefsRole(string entKey, string targetRole) =>
            fieldDefs.TryGetValue(entKey, out var fs) && fs.Values.Any(f =>
                Str(f, "type") == "reference" && Str(f, "targetEntity") is { } te && role.GetValueOrDefault(te) == targetRole);
        bool HasFieldRole(string entKey, string fieldRole) =>
            fieldDefs.TryGetValue(entKey, out var fs) && fs.Values.Any(f => Str(f, "role") == fieldRole);

        // A functional forms app needs all four roles — template + questions, plus somewhere to collect
        // (formResponse) and store (formAnswer) submissions. Without the latter two the form can't be taken.
        foreach (var required in new[] { "formTemplate", "formField", "formResponse", "formAnswer" })
            if (!byRole.ContainsKey(required))
                errors.Add($"SEMANTIC: forms module enabled but no entity has role '{required}'");

        // The three roles a question needs so the SERVER can read it the way the browser does. The
        // renderer used to infer all of them structurally — "the first json field is the choices, the
        // first boolean is required" — which is a fine heuristic for a form somebody is looking at and
        // an unacceptable contract for one a stranger can post to unattended. Required in particular
        // was enforced only in the browser, which is to say not at all.
        string? FieldRoleTyped(string entKey, string fieldRole) =>
            fieldDefs.TryGetValue(entKey, out var fs)
                ? fs.Values.FirstOrDefault(f => Str(f, "role") == fieldRole) is { } f ? Str(f, "type") : null
                : null;

        foreach (var e in byRole.GetValueOrDefault("formField", new()))
        {
            if (!RefsRole(e, "formTemplate")) errors.Add($"SEMANTIC: formField '{e}' must have a reference to the formTemplate entity");
            if (!HasFieldRole(e, "answerType")) errors.Add($"SEMANTIC: formField '{e}' must have a field with role 'answerType'");

            if (FieldRoleTyped(e, "answerRequired") is { } reqType && reqType != "boolean")
                errors.Add($"SEMANTIC: formField '{e}' has a role:'answerRequired' field of type '{reqType}' — "
                         + "a question is required or it is not, so it must be a 'boolean' field");
            if (FieldRoleTyped(e, "answerOptions") is { } optType && optType != "json")
                errors.Add($"SEMANTIC: formField '{e}' has a role:'answerOptions' field of type '{optType}' — "
                         + "it holds the LIST of choices a question offers, so it must be a 'json' field");
            if (FieldRoleTyped(e, "respondentEmail") is { } mailType && mailType != "boolean")
                errors.Add($"SEMANTIC: formField '{e}' has a role:'respondentEmail' field of type '{mailType}' — "
                         + "it MARKS the question that asks for the submitter's address rather than holding one, "
                         + "so it must be a 'boolean' field");
        }
        foreach (var e in byRole.GetValueOrDefault("formResponse", new()))
            if (!RefsRole(e, "formTemplate")) errors.Add($"SEMANTIC: formResponse '{e}' must have a reference to the formTemplate entity");
        foreach (var e in byRole.GetValueOrDefault("formAnswer", new()))
        {
            if (!RefsRole(e, "formResponse")) errors.Add($"SEMANTIC: formAnswer '{e}' must reference the formResponse entity");
            if (!RefsRole(e, "formField")) errors.Add($"SEMANTIC: formAnswer '{e}' must reference the formField entity");
            if (!HasFieldRole(e, "answerValue")) errors.Add($"SEMANTIC: formAnswer '{e}' must have a field with role 'answerValue'");
        }

        ValidateFormProjection(byRole, role, fieldDefs, errors);
    }

    /// <summary>The intake capability: a form whose submissions are PROJECTED into another entity
    /// (a helpdesk intake form creating a ticket).
    ///
    /// <para>Only the shape is checkable here. WHICH entity a template targets, and which of its
    /// fields each question fills, are runtime DATA — an author declares the capability, a user
    /// builds the form. So this validates the declarations and the submit endpoint validates the
    /// data, which is the honest split rather than pretending the gate can see rows.</para></summary>
    private static void ValidateFormProjection(Dictionary<string, List<string>> byRole,
        Dictionary<string, string> role, Dictionary<string, Dictionary<string, JsonObject>> fieldDefs,
        List<string> errors)
    {
        var templates = byRole.GetValueOrDefault("formTemplate", new());

        foreach (var (entKey, fields) in fieldDefs)
        {
            var isTemplate = templates.Contains(entKey);
            foreach (var (fkey, f) in fields)
            {
                var frole = Str(f, "role");

                // `mapsTo` says where a value LANDS on the created record. Only a template has a
                // target to land on.
                if (Str(f, "mapsTo") is not null && !isTemplate)
                    errors.Add($"SEMANTIC: '{entKey}.{fkey}' declares 'mapsTo', which only means something on a "
                             + "formTemplate entity (it routes the template's own value onto the record a submission creates)");

                if (frole == "targetEntity")
                {
                    if (!isTemplate)
                        errors.Add($"SEMANTIC: '{entKey}.{fkey}' has role 'targetEntity', which belongs on a formTemplate — "
                                 + "it names the entity a SUBMISSION creates");
                    else if (Str(f, "type") != "text")
                        errors.Add($"SEMANTIC: '{entKey}.{fkey}' has role 'targetEntity' so it must be a 'text' field "
                                 + $"(it holds an entity key), not '{Str(f, "type")}'");
                }
                if (frole == "mapsTo")
                {
                    if (role.GetValueOrDefault(entKey) != "formField")
                        errors.Add($"SEMANTIC: '{entKey}.{fkey}' has role 'mapsTo', which belongs on a formField — "
                                 + "it names which field of the target entity the question fills");
                    else if (Str(f, "type") != "text")
                        errors.Add($"SEMANTIC: '{entKey}.{fkey}' has role 'mapsTo' so it must be a 'text' field "
                                 + $"(it holds a field key), not '{Str(f, "type")}'");
                }
            }
        }

        // A template that can't say what it targets can't route anything, so per-question mapping and
        // per-template defaults would be dead weight. Say so at author time rather than at 3am.
        foreach (var t in templates)
        {
            var hasTarget = fieldDefs.GetValueOrDefault(t, new()).Values.Any(f => Str(f, "role") == "targetEntity");
            if (hasTarget) continue;
            if (fieldDefs.GetValueOrDefault(t, new()).Values.Any(f => Str(f, "mapsTo") is not null))
                errors.Add($"SEMANTIC: formTemplate '{t}' routes fields with 'mapsTo' but has no field with role "
                         + "'targetEntity', so there is nothing to route them onto");
            foreach (var ff in byRole.GetValueOrDefault("formField", new()))
                if (fieldDefs.GetValueOrDefault(ff, new()).Values.Any(f => Str(f, "role") == "mapsTo"))
                {
                    errors.Add($"SEMANTIC: formField '{ff}' maps answers onto a target entity, but formTemplate '{t}' "
                             + "has no field with role 'targetEntity' to name one");
                    break;
                }
        }
    }

    // ---- behavior layer: commands, processes, effects, conditions -------------------------------

    /// <summary>Shared lookups for the behavior validators.</summary>
    private sealed record BehaviorCtx(
        HashSet<string> Entities,
        Func<string?, string?, bool> FieldExists,
        Func<string?, string?, string?> FieldType,
        Dictionary<string, Dictionary<string, JsonObject>> FieldDefs,
        Dictionary<string, string> StatusFieldOf,
        Dictionary<string, HashSet<string>>? StateKeysByEntity = null,
        // The entity objects themselves. `prev()` has to ask whether its entity declares a series,
        // which is a property of the entity rather than of any field.
        Dictionary<string, JsonObject>? EntityDefs = null);

    // A condition's comparison value accepts whatever ExprTokens knows: the actor (either spelling),
    // {{today}}/{{now}}, and their offsets. The grammar lives in one place so a token can never pass
    // the gate and then fail to resolve at run time.
    // Effect templates additionally carry names, which a comparison has no use for.
    private static readonly HashSet<string> EffectOnlyTokens = new() { "actor.name", "app.name" };

    /// <summary>Event names the runtime derives from every write on its own — declaring one as
    /// <c>command.emits</c> adds nothing.</summary>
    private static readonly HashSet<string> DerivedEventSuffixes = new(StringComparer.Ordinal)
        { "created", "updated", "deleted" };

    private static readonly System.Text.RegularExpressions.Regex TemplateToken =
        new(@"\{\{\s*([^}]+?)\s*\}\}", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Commands: unique per entity; entity/when/input/effects resolve. Fills
    /// <paramref name="commandsByEntity"/> for grant + hub-action validation. A command may have empty
    /// effects only when a process transition binds it (the state change is the effect).</summary>
    private static void ValidateCommands(JsonNode? commands, BehaviorCtx ctx,
        HashSet<string> transitionBoundCommands, Dictionary<string, Dictionary<string, JsonObject>> commandsByEntity,
        List<string> errors)
    {
        var emittedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cn in Arr(commands))
        {
            if (cn is not JsonObject cmd) continue;
            var key = Str(cmd, "key") ?? "";
            var ent = Str(cmd, "entity");
            var where = $"command '{key}'";
            if (ent == null || !ctx.Entities.Contains(ent))
            { errors.Add($"SEMANTIC: {where} targets unknown entity '{ent}'"); continue; }

            var perEntity = commandsByEntity.TryGetValue(ent, out var m) ? m : commandsByEntity[ent] = new();
            if (!perEntity.TryAdd(key, cmd))
                errors.Add($"SEMANTIC: duplicate command key '{key}' on entity '{ent}'");

            if (cmd["when"] is JsonObject when) ValidateCondition(when, ent, where, ctx, errors);

            // `emits` names this command announces to other apps. Two commands claiming the same name
            // would make a subscription ambiguous — the whole point of a semantic name is that it
            // identifies ONE thing that happened — so uniqueness is enforced app-wide, not per entity.
            // A name that merely restates what the runtime already emits is called out as redundant
            // rather than silently duplicated onto the same write.
            foreach (var en in Arr(cmd["emits"]))
            {
                if (en?.GetValue<string>() is not { } name) continue;
                if (!emittedNames.Add(name))
                    errors.Add($"SEMANTIC: {where} emits '{name}', which another command already emits — a semantic event name must identify one thing");
                if (DerivedEventSuffixes.Contains(name[(name.IndexOf('.') + 1)..]))
                    errors.Add($"SEMANTIC: {where} emits '{name}', which the runtime already emits for every write — declare 'emits' only for a name the schema cannot derive");
            }

            if (cmd["input"] is JsonObject input)
            {
                var declared = new HashSet<string>();
                foreach (var fn in Arr(input["fields"]))
                    if (fn?.GetValue<string>() is { } fk)
                    {
                        declared.Add(fk);
                        if (BaseFields.Contains(fk))
                            errors.Add($"SEMANTIC: {where} input field '{fk}' is a runtime-provided base field and cannot be collected");
                        else if (!ctx.FieldExists(ent, fk))
                            errors.Add($"SEMANTIC: {where} input field '{fk}' is not a field of '{ent}'");
                    }
                foreach (var rn in Arr(input["required"]))
                    if (rn?.GetValue<string>() is { } rk && !declared.Contains(rk))
                        errors.Add($"SEMANTIC: {where} input.required '{rk}' is not one of input.fields");
            }

            if (Arr(cmd["effects"]).Count == 0 && !transitionBoundCommands.Contains(ent + "|" + key))
                errors.Add($"SEMANTIC: {where} has no effects — a command needs at least one effect unless a process transition binds it");
            ValidateEffects(cmd["effects"], ent, where, ctx, errors);

            foreach (var tmpl in new[] { Str(cmd, "successMessage"), Str(cmd["confirm"], "title"), Str(cmd["confirm"], "message") })
                if (tmpl != null) ValidateTemplate(tmpl, ent, where, ctx, errors);
        }
    }

    /// <summary>Processes: one per entity; stateField is the entity's role:'status' select; states are
    /// set-equal to that field's options; initial/from/to are known states; terminals have no outgoing
    /// transition; bound commands resolve and aren't shared; requiredFields resolve.</summary>
    private static void ValidateProcesses(JsonNode? processes, BehaviorCtx ctx,
        Dictionary<string, Dictionary<string, JsonObject>> commandsByEntity, List<string> errors)
    {
        var seenProc = new HashSet<string>();
        var entityHasProcess = new HashSet<string>();
        var boundBy = new Dictionary<string, string>();   // "entity|command" -> transition key
        foreach (var pn in Arr(processes))
        {
            if (pn is not JsonObject p) continue;
            var key = Str(p, "key") ?? "";
            var where = $"process '{key}'";
            if (!seenProc.Add(key)) errors.Add($"SEMANTIC: duplicate process key '{key}'");
            var ent = Str(p, "entity");
            if (ent == null || !ctx.Entities.Contains(ent))
            { errors.Add($"SEMANTIC: {where} targets unknown entity '{ent}'"); continue; }
            if (!entityHasProcess.Add(ent))
                errors.Add($"SEMANTIC: entity '{ent}' has more than one process — at most one is allowed (process '{key}')");

            var stateField = Str(p, "stateField");
            var statusField = ctx.StatusFieldOf.GetValueOrDefault(ent);
            if (stateField == null || !ctx.FieldExists(ent, stateField))
                errors.Add($"SEMANTIC: {where} stateField '{stateField}' is not a field of '{ent}'");
            else if (statusField == null)
                errors.Add($"SEMANTIC: {where} entity '{ent}' has no role:'status' field — mark '{stateField}' with role:'status'");
            else if (stateField != statusField)
                errors.Add($"SEMANTIC: {where} stateField '{stateField}' must be the entity's role:'status' field ('{statusField}')");

            var stateKeys = new HashSet<string>();
            foreach (var sn in Arr(p["states"]))
                if (sn is JsonObject s && Str(s, "key") is { } sk && !stateKeys.Add(sk))
                    errors.Add($"SEMANTIC: {where} has duplicate state '{sk}'");
            // Entry is either a state key or CONDITIONAL entry { rules:[{when,state}], fallback }.
            // Whichever it is, every state it can land on must be a real state, and every guard must
            // resolve against the entity (including a one-hop `path` to a referenced record).
            string? initial;
            if (p["initialState"] is JsonObject entry)
            {
                initial = Str(entry, "fallback");
                if (initial == null || !stateKeys.Contains(initial))
                    errors.Add($"SEMANTIC: {where} initialState fallback '{initial}' is not one of its states");
                var ri = 0;
                foreach (var rn in Arr(entry["rules"]))
                {
                    if (rn is not JsonObject rule) continue;
                    var rw = $"{where} initialState rule[{ri++}]";
                    if (Str(rule, "state") is not { } rs || !stateKeys.Contains(rs))
                        errors.Add($"SEMANTIC: {rw} state '{Str(rule, "state")}' is not one of its states");
                    if (rule["when"] is JsonObject rwhen && ent != null)
                        ValidateLeafGuard(rwhen, ent, rw, ctx, errors);
                }
            }
            else
            {
                initial = Str(p, "initialState");
                if (initial == null || !stateKeys.Contains(initial))
                    errors.Add($"SEMANTIC: {where} initialState '{initial}' is not one of its states");
            }

            // THE STATES ARE THE SOURCE OF TRUTH for a governed field. AppCompiler.CanonicalizeProcesses
            // overwrites the field's `options` from the states (value/label/color) and stamps `default`
            // from `initialState`, so anything authored there is discarded — which made the old
            // "states must exactly match the options" rule busywork: it forced the author to keep two
            // copies in sync and then threw one away. Declaring either is now the error, not just
            // declaring it differently. Put a state's colour on the state (`states[].color`).
            if (stateField != null && statusField == stateField
                && ctx.FieldDefs.TryGetValue(ent, out var fds) && fds.TryGetValue(stateField, out var sfDef))
            {
                if (sfDef["options"] is JsonArray { Count: > 0 })
                    errors.Add($"SEMANTIC: {where} governs '{ent}.{stateField}', so its states ARE that field's options — remove the field's 'options' (put each state's colour on 'states[].color')");
                if (sfDef["default"] is not null)
                    errors.Add($"SEMANTIC: {where} governs '{ent}.{stateField}', so 'initialState' ({initial ?? "?"}) IS that field's default — remove the field's 'default'");
                // Review item 10: `initial` on a governed field is business logic hidden on a field,
                // in a second place, in a shape the process cannot see. Conditional ENTRY says the
                // same thing where the states live — and the compiler lowers it back onto `initial`,
                // so the runtime keeps exactly one mechanism.
                if (sfDef["initial"] is not null)
                    errors.Add($"SEMANTIC: {where} governs '{ent}.{stateField}', so its starting value belongs to the process — move the field's 'initial' rules to initialState: {{ rules:[{{when, state}}], fallback }}");
            }

            var terminal = Arr(p["states"]).OfType<JsonObject>()
                .Where(s => s["terminal"]?.GetValue<bool>() == true)
                .Select(s => Str(s, "key")).Where(k => k != null).Select(k => k!).ToHashSet();

            var seenTrans = new HashSet<string>();
            var cmds = commandsByEntity.GetValueOrDefault(ent) ?? new();
            foreach (var tn in Arr(p["transitions"]))
            {
                if (tn is not JsonObject t) continue;
                var tkey = Str(t, "key") ?? "";
                if (!seenTrans.Add(tkey)) errors.Add($"SEMANTIC: {where} has duplicate transition key '{tkey}'");
                foreach (var fn in Arr(t["from"]))
                {
                    if (fn?.GetValue<string>() is not { } fk) continue;
                    if (!stateKeys.Contains(fk)) errors.Add($"SEMANTIC: {where} transition '{tkey}' from '{fk}' is not a state");
                    else if (terminal.Contains(fk)) errors.Add($"SEMANTIC: {where} transition '{tkey}' leaves terminal state '{fk}'");
                }
                var to = Str(t, "to");
                if (to == null || !stateKeys.Contains(to)) errors.Add($"SEMANTIC: {where} transition '{tkey}' to '{to}' is not a state");
                if (Str(t, "command") is { } tc)
                {
                    if (!cmds.ContainsKey(tc)) errors.Add($"SEMANTIC: {where} transition '{tkey}' references unknown command '{tc}' on '{ent}'");
                    var bkey = ent + "|" + tc;
                    if (boundBy.TryGetValue(bkey, out var other))
                        errors.Add($"SEMANTIC: {where} command '{tc}' backs two transitions ('{other}' and '{tkey}') — a command may back only one");
                    else boundBy[bkey] = tkey;
                }
                else
                {
                    // REGISTER THE COMMAND THE COMPILER WILL SYNTHESIZE, so a block may reference it.
                    //
                    // Every command-less transition gets one (AppCompiler.SynthesizeCommands), but
                    // that runs after this — so without registering it here, an `action` block
                    // pointing at a perfectly real button is rejected for naming a command that "is
                    // not a command on this entity". An agent hit exactly that and worked around it
                    // by authoring a duplicate, ending up with two identical buttons on the record.
                    //
                    // The KEY comes from ProcessCommands so the gate and the compiler cannot drift.
                    // Registered into commandsByEntity, which is built before the block checks run.
                    // If an authored command already holds this key the compiler suffixes its own
                    // (`_2`) — and that is consistent, because the key registered here then really
                    // does exist, as the authored one, which is what a reference should resolve to.
                    var synthesized = ProcessCommands.SynthesizedKey(ent, tkey);
                    var forEntity = commandsByEntity.TryGetValue(ent, out var em)
                        ? em : commandsByEntity[ent] = new Dictionary<string, JsonObject>();

                    if (!forEntity.ContainsKey(synthesized))
                    {
                        forEntity[synthesized] = new JsonObject
                        {
                            ["key"] = synthesized,
                            ["entity"] = ent,
                            ["label"] = Str(t, "label"),
                            ["synthesized"] = true,
                        };
                    }
                    cmds = forEntity;
                }
                if (t["when"] is JsonObject tw) ValidateCondition(tw, ent, $"{where} transition '{tkey}'", ctx, errors);
                foreach (var rf in Arr(t["requiredFields"]))
                    if (rf?.GetValue<string>() is { } rfk && !ctx.FieldExists(ent, rfk))
                        errors.Add($"SEMANTIC: {where} transition '{tkey}' requiredFields '{rfk}' is not a field of '{ent}'");
            }

            // REACHABILITY. Every declared state must be reachable from an ENTRY state by following
            // transitions. Checking only that a state is some transition's `to` is not enough: a
            // disconnected pair (ghost_a -> ghost_b -> ghost_a) satisfies that while neither can
            // ever be entered. So seed with every possible entry and walk to a fixpoint.
            // Live 2026-08-02 (MeetingPrep): `close_action_item` went TO 'closed' while
            // `reopen_action_item` went FROM 'reopened' — nothing led to 'reopened', so a closed
            // action item could never be reopened. The domain critic reported it as medium, the
            // pipeline dropped the finding, and it shipped. A deterministic property of the
            // document belongs here, where it repairs inside the existing loop for free.
            // Entry is a state key OR conditional { rules:[{when,state}], fallback } — collect BOTH
            // forms or a legitimately-conditional start state reads as unreachable (room-booking's
            // 'pending'). Runs only once entry is sound: an invalid initialState already errored
            // above, and reporting every state as unreachable on top of that is noise.
            var entryStates = new HashSet<string>();
            if (p["initialState"] is JsonObject entry2)
            {
                if (Str(entry2, "fallback") is { } fb && stateKeys.Contains(fb)) entryStates.Add(fb);
                foreach (var rn in Arr(entry2["rules"]))
                    if (Str(rn as JsonObject, "state") is { } rs && stateKeys.Contains(rs)) entryStates.Add(rs);
            }
            else if (initial != null && stateKeys.Contains(initial)) entryStates.Add(initial);

            if (entryStates.Count > 0)
            {
                // `from` is required with minItems:1 (schema), so there is no wildcard/absent form.
                var edges = Arr(p["transitions"]).OfType<JsonObject>()
                    .Select(t => (
                        Key: Str(t, "key") ?? "",
                        From: Arr(t["from"]).Select(f => f?.GetValue<string>())
                                            .Where(f => f is not null).Select(f => f!).ToList(),
                        To: Str(t, "to")))
                    .ToList();

                var reachable = new HashSet<string>(entryStates);
                bool grew;
                do
                {
                    grew = false;
                    foreach (var e in edges)
                        if (e.To is { } to && stateKeys.Contains(to) && e.From.Any(reachable.Contains)
                            && reachable.Add(to))
                            grew = true;
                } while (grew);

                foreach (var dead in stateKeys.Where(k => !reachable.Contains(k))
                                              .OrderBy(k => k, StringComparer.Ordinal))
                    errors.Add($"SEMANTIC: {where} state '{dead}' can never be reached — no path of "
                             + "transitions leads to it from an initial state. Add a transition into "
                             + "it, or drop the state");
                foreach (var e in edges.Where(e => e.From.Count > 0 && !e.From.Any(reachable.Contains)))
                    errors.Add($"SEMANTIC: {where} transition '{e.Key}' can never fire — none of its "
                             + $"'from' states ({string.Join("/", e.From)}) is reachable");
            }
        }
    }

    /// <summary>The shared typed-effect validator (commands + workflows). <paramref name="ctxEntity"/> is
    /// the record the effect runs against (the command's entity / the workflow trigger's entity).</summary>
    private static void ValidateEffects(JsonNode? effects, string? ctxEntity, string where,
        BehaviorCtx ctx, List<string> errors, string? triggerEvent = null)
    {
        var i = 0;

        // What a createRecord above this point inserted, so {{created.*}} resolves against the right
        // entity and is refused where nothing has been created yet. Updated at the END of each
        // iteration: an effect cannot name what it is itself creating.
        CreatedScope? created = null;

        foreach (var en in Arr(effects))
        {
            var ew = $"{where} effect[{i++}]";
            if (en is not JsonObject eff) continue;

            // The effect's own guard, resolved against the same record the effect writes.
            if (eff["when"] is JsonObject effectWhen)
            {
                if (ctxEntity is null)
                    errors.Add($"SEMANTIC: {ew} has a 'when' guard but there is no entity to resolve it against");
                else ValidateCondition(effectWhen, ctxEntity, ew, ctx, errors);
            }

            switch (Str(eff, "type"))
            {
                case "updateRecord":
                {
                    var targetEntity = ctxEntity;
                    if (eff["target"] is JsonObject tgt && Str(tgt, "field") is { } tf)
                    {
                        if (!ctx.FieldExists(ctxEntity, tf) || ctx.FieldType(ctxEntity, tf) != "reference")
                            errors.Add($"SEMANTIC: {ew} target.field '{tf}' must be a reference field on '{ctxEntity}'");
                        else targetEntity = TargetEntityOf(ctx, ctxEntity, tf) ?? ctxEntity;
                    }
                    // set keys resolve on the target record; {{record.x}} templates on the triggering record.
                    ValidateSet(eff["set"], targetEntity, ctxEntity, ew, ctx, errors, created: created);
                    break;
                }
                case "deleteRecord":
                {
                    var toSelf = true;
                    if (eff["target"] is JsonObject dtgt && Str(dtgt, "field") is { } df)
                    {
                        toSelf = false;
                        if (!ctx.FieldExists(ctxEntity, df) || ctx.FieldType(ctxEntity, df) != "reference")
                            errors.Add($"SEMANTIC: {ew} target.field '{df}' must be a reference field on '{ctxEntity}'");
                    }

                    // AN UNCONDITIONAL SELF-DELETE ON A WRITE TRIGGER EMPTIES THE TABLE. Every record
                    // created fires the workflow, the workflow deletes it, and the application reports
                    // every save as successful while nothing is ever there. A command may do this —
                    // somebody pressed a button that says Delete — but a rule that runs on its own
                    // has to say which records it means.
                    if (toSelf && eff["when"] is not JsonObject
                        && triggerEvent is "record.created" or "record.updated")
                        errors.Add($"SEMANTIC: {ew} deletes 'self' on every {triggerEvent} with no "
                                 + "'when' guard, which removes every record of this entity as fast as "
                                 + "anybody can make one. Give it a condition, or delete from a command "
                                 + "somebody presses");
                    break;
                }
                case "createRecord":
                {
                    var te = Str(eff, "entity");
                    if (te == null || !ctx.Entities.Contains(te))
                    { errors.Add($"SEMANTIC: {ew} createRecord targets unknown entity '{te}'"); break; }
                    ValidateSet(eff["set"], te, ctxEntity, ew, ctx, errors, created: created);
                    if (ctx.FieldDefs.TryGetValue(te, out var tfs))
                    {
                        var setKeys = (eff["set"] as JsonObject)?.Select(kv => kv.Key).ToHashSet() ?? new();
                        foreach (var (fk, fd) in tfs)
                            if (fd["required"]?.GetValue<bool>() == true && !setKeys.Contains(fk))
                                errors.Add($"SEMANTIC: {ew} createRecord on '{te}' does not set required field '{fk}'");
                    }
                    break;
                }
                case "createForEach":
                {
                    var te = Str(eff, "entity");
                    if (te == null || !ctx.Entities.Contains(te))
                    { errors.Add($"SEMANTIC: {ew} createForEach targets unknown entity '{te}'"); break; }
                    if (eff["source"] is not JsonObject src)
                    { errors.Add($"SEMANTIC: {ew} createForEach has no source"); break; }

                    var srcEntity = Str(src, "entity");
                    var hasRange = src["range"] is JsonObject;
                    if ((srcEntity is null) == !hasRange)
                    {
                        errors.Add($"SEMANTIC: {ew} createForEach source needs exactly one of 'entity' "
                                 + "(iterate records) or 'range' (iterate dates)");
                        break;
                    }
                    if (srcEntity is not null && !ctx.Entities.Contains(srcEntity))
                    { errors.Add($"SEMANTIC: {ew} createForEach source entity '{srcEntity}' is unknown"); break; }
                    foreach (var f in (src["filters"] as JsonArray ?? new()).OfType<JsonObject>())
                        if (Str(f, "field") is { } ff && srcEntity is not null && !ctx.FieldExists(srcEntity, ff))
                            errors.Add($"SEMANTIC: {ew} createForEach source filter field '{ff}' is not a field of '{srcEntity}'");

                    // The set map, which until now was read only for WHICH keys it writes and never
                    // for whether they are fields or whether their templates resolve. That made
                    // createForEach the one effect where a misspelt token shipped: it writes a blank
                    // and the rows appear, so the failure looks like missing data rather than a
                    // broken definition.
                    ValidateSet(eff["set"], te, ctxEntity, ew, ctx, errors,
                        new SourceScope(srcEntity, hasRange), created);

                    // Every key must be a field of the entity being CREATED and must be set — a key
                    // naming something the effect never writes cannot identify anything, so the effect
                    // would duplicate its whole output on a second run while claiming to be idempotent.
                    var written = (eff["set"] as JsonObject)?.Select(kv => kv.Key).ToHashSet() ?? new();
                    foreach (var k in (eff["key"] as JsonArray ?? new()).OfType<JsonValue>()
                                 .Select(v => v.TryGetValue<string>(out var s2) ? s2 : null).OfType<string>())
                    {
                        if (!ctx.FieldExists(te, k))
                            errors.Add($"SEMANTIC: {ew} createForEach key '{k}' is not a field of '{te}'");
                        else if (!written.Contains(k))
                            errors.Add($"SEMANTIC: {ew} createForEach key '{k}' is never set, so it cannot "
                                     + "identify a row — running this twice would duplicate everything");
                    }

                    if (ctx.FieldDefs.TryGetValue(te, out var eachFields))
                        foreach (var (fk, fd) in eachFields)
                            if (fd["required"]?.GetValue<bool>() == true && !written.Contains(fk))
                                errors.Add($"SEMANTIC: {ew} createForEach on '{te}' does not set required field '{fk}'");
                    break;
                }
                case "notify":
                    if (Str(eff, "to") is { } nto) ValidateRecipient(nto, ctxEntity, ew, ctx, errors, created);
                    foreach (var s in new[] { Str(eff, "title"), Str(eff, "message") })
                        if (s != null) ValidateTemplate(s, ctxEntity, ew, ctx, errors, created: created);
                    break;
                case "email":
                    if (Str(eff, "to") is { } eto) ValidateRecipient(eto, ctxEntity, ew, ctx, errors, created);
                    foreach (var s in new[] { Str(eff, "subject"), Str(eff, "body") })
                        if (s != null) ValidateTemplate(s, ctxEntity, ew, ctx, errors, created: created);
                    break;
                case "webhook":
                    if (Str(eff, "url") is not { } url || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                        errors.Add($"SEMANTIC: {ew} webhook url must be https");
                    else if (FabricatedHost(url) is { } host)
                        errors.Add($"SEMANTIC: {ew} webhook points at '{host}', which is not a real service. "
                                 + "There is no automation platform behind this app to call. If a figure has to "
                                 + "be derived, make it a computed field; if it cannot be derived, leave it as a "
                                 + "field somebody fills — do not add a button that claims to calculate it.");
                    break;
                case "enrich":
                {
                    // `self` is the only legal target (the schema enforces that), so what is left to
                    // check is that the record it resolves to can actually HOLD the result: the
                    // enrichment writer promotes onto enr_* fields, and a record type without them
                    // would queue a paid job whose output has nowhere to land.
                    if (Str(eff, "target") is { } et && et != "self")
                    { errors.Add($"SEMANTIC: {ew} enrich target must be 'self'"); break; }
                    if (ctxEntity is { } ce && !ctx.FieldExists(ce, "enr_state"))
                        errors.Add($"SEMANTIC: {ew} enrich needs an 'enr_state' field on '{ce}' to report into — "
                                 + "add the enrichment fields to the entity, or drop the effect");
                    break;
                }
            }

            // AFTER the effect, so it cannot name what it is itself creating. The most recent create
            // wins: two creates then one reference means the reference points at the second, which is
            // what reading the list top to bottom says it should.
            if (Str(eff, "type") == "createRecord" && Str(eff, "entity") is { } madeEntity
                && ctx.Entities.Contains(madeEntity))
                created = new CreatedScope(madeEntity);
        }
    }

    private static string? TargetEntityOf(BehaviorCtx ctx, string? entity, string field) =>
        entity != null && ctx.FieldDefs.TryGetValue(entity, out var m) && m.TryGetValue(field, out var fd)
            ? Str(fd, "targetEntity") : null;

    /// <summary>The legal values of a select. For a PROCESS-GOVERNED field these live on the process's
    /// states, not on the field — the field carries no options at all (the compiler writes them from
    /// the states). Every caller asking "is this a valid option?" has to look in both places, so the
    /// lookup does it once here rather than each caller remembering.</summary>
    private static HashSet<string> OptionValuesOf(BehaviorCtx ctx, string? entity, string field)
    {
        if (entity == null) return new();
        if (ctx.StatusFieldOf.GetValueOrDefault(entity) == field
            && ctx.StateKeysByEntity?.GetValueOrDefault(entity) is { Count: > 0 } states)
            return states;
        return ctx.FieldDefs.TryGetValue(entity, out var m) && m.TryGetValue(field, out var fd)
            ? (fd["options"] as JsonArray ?? new()).OfType<JsonObject>()
                .Select(o => Str(o, "value")).Where(v => v != null).Select(v => v!).ToHashSet()
            : new();
    }

    /// <summary>Validate an effect's <c>set</c>: keys resolve on <paramref name="keyEntity"/> (the record
    /// written), a literal on a select must be a valid option, and <c>{{record.x}}</c> templates resolve
    /// against <paramref name="contextEntity"/> (the triggering record).</summary>
    private static void ValidateSet(JsonNode? set, string? keyEntity, string? contextEntity, string where,
        BehaviorCtx ctx, List<string> errors, SourceScope? source = null, CreatedScope? created = null)
    {
        if (set is not JsonObject obj) return;   // structural layer requires it to be an object
        foreach (var (k, v) in obj)
        {
            if (BaseFields.Contains(k)) { errors.Add($"SEMANTIC: {where} set '{k}' is a runtime-provided base field"); continue; }
            if (!ctx.FieldExists(keyEntity, k)) { errors.Add($"SEMANTIC: {where} set '{k}' is not a field of '{keyEntity}'"); continue; }
            if (v is JsonObject nested && nested["pick"] is not null)
            { ValidatePick(nested["pick"], k, contextEntity, where, ctx, errors, source, created); continue; }
            if (v is JsonValue jv && jv.TryGetValue<string>(out var sval))
            {
                if (sval.Contains("{{")) { ValidateTemplate(sval, contextEntity, where, ctx, errors, source, created); continue; }
                // OptionValuesOf already resolves a governed field to its process states.
                if (ctx.FieldType(keyEntity, k) == "select"
                    && !OptionValuesOf(ctx, keyEntity, k).Contains(sval))
                    errors.Add($"SEMANTIC: {where} set '{k}' = '{sval}' is not a valid option of the '{k}' select");
            }
        }
    }

    /// <summary>
    /// A <c>pick</c> inside a set value: the rule that chooses one record to write into a field.
    ///
    /// <para>Checked at author time because the runtime CANNOT report these. A pick whose sort field
    /// does not exist still returns a record — just the wrong one, silently, forever, at 8am on a
    /// Monday. "Whose turn is it" answered wrongly and confidently is the worst failure this feature
    /// has, and the only place to catch it is here.</para>
    /// </summary>
    private static void ValidatePick(JsonNode? pickNode, string field, string? ctxEntity, string where,
        BehaviorCtx ctx, List<string> errors, SourceScope? source = null, CreatedScope? created = null)
    {
        if (pickNode is not JsonObject pick)
        { errors.Add($"SEMANTIC: {where} set '{field}' pick must be an object"); return; }

        var entity = Str(pick, "entity");
        if (entity is null || !ctx.Entities.Contains(entity))
        { errors.Add($"SEMANTIC: {where} set '{field}' pick reads unknown entity '{entity}'"); return; }

        foreach (var f in (pick["filters"] as JsonArray ?? new()).OfType<JsonObject>())
        {
            if (Str(f, "field") is { } ff && !ctx.FieldExists(entity, ff))
                errors.Add($"SEMANTIC: {where} set '{field}' pick filter field '{ff}' is not a field of '{entity}'");
            if (Str(f, "value") is { } fv && fv.Contains("{{"))
                ValidateTemplate(fv, ctxEntity, where, ctx, errors, source, created);
        }

        // The ordering IS the rule — "the one who went longest without a turn" is a sort, not a
        // filter. A pick with no sort takes an arbitrary row and calls it a decision.
        var sorts = (pick["sort"] as JsonArray ?? new()).OfType<JsonObject>().ToList();
        if (sorts.Count == 0)
            errors.Add($"SEMANTIC: {where} set '{field}' pick has no sort — without one it takes an "
                     + "arbitrary record, which is not a rule anybody can predict or explain");
        foreach (var s in sorts)
            if (Str(s, "field") is { } sf && !ctx.FieldExists(entity, sf))
                errors.Add($"SEMANTIC: {where} set '{field}' pick sorts by '{sf}', which is not a field of '{entity}'");

        if (Str(pick, "field") is { } picked && picked != "id" && !ctx.FieldExists(entity, picked))
            errors.Add($"SEMANTIC: {where} set '{field}' pick reads '{picked}', which is not a field of '{entity}'");
    }

    private static void ValidateRecipient(string to, string? ctxEntity, string where, BehaviorCtx ctx,
        List<string> errors, CreatedScope? created = null)
    {
        if (to.Contains("{{")) { ValidateTemplate(to, ctxEntity, where, ctx, errors, created: created); return; }
        if (!to.Contains('@')) errors.Add($"SEMANTIC: {where} recipient '{to}' must be a template (e.g. {{{{record.requester}}}}) or an email address");
    }

    private static void ValidateTemplate(string text, string? ctxEntity, string where, BehaviorCtx ctx,
        List<string> errors, SourceScope? source = null, CreatedScope? created = null)
    {
        foreach (System.Text.RegularExpressions.Match mm in TemplateToken.Matches(text))
        {
            var token = mm.Groups[1].Value.Trim();
            if (token.StartsWith("record.", StringComparison.Ordinal))
            {
                var f = token["record.".Length..];
                if (!ctx.FieldExists(ctxEntity, f))
                    errors.Add($"SEMANTIC: {where} template token '{{{{{token}}}}}' — '{f}' is not a field of '{ctxEntity}'");
            }
            else if (token.StartsWith("created.", StringComparison.Ordinal))
            {
                // The record an earlier effect in this same list inserted.
                var f = token["created.".Length..];
                if (created is null)
                    errors.Add($"SEMANTIC: {where} template token '{{{{{token}}}}}' — nothing has been "
                             + "created yet at this point in the list; '{{created.*}}' names the record "
                             + "a createRecord ABOVE this effect inserted");
                else if (!ctx.FieldExists(created.Entity, f))
                    errors.Add($"SEMANTIC: {where} template token '{{{{{token}}}}}' — '{f}' is not a "
                             + $"field of '{created.Entity}', the entity just created");
            }
            else if (token.StartsWith("source.", StringComparison.Ordinal))
            {
                // The row being iterated. Unchecked until now, which made this the one template
                // prefix an author could misspell and still ship: it resolves to nothing at run
                // time and writes a blank, so the effect appears to work and the column is empty.
                var f = token["source.".Length..];
                if (source is null)
                    errors.Add($"SEMANTIC: {where} template token '{{{{{token}}}}}' — there is no source "
                             + "row here; '{{source.*}}' means the row being iterated and only a "
                             + "createForEach iterates anything");
                else if (source.IsRange)
                {
                    if (!RangeRowFields.Contains(f))
                        errors.Add($"SEMANTIC: {where} template token '{{{{{token}}}}}' — a generated "
                                 + $"date row has {string.Join(", ", RangeRowFields.Order(StringComparer.Ordinal).Select(r => "'" + r + "'"))} "
                                 + "and nothing else");
                }
                else if (!ctx.FieldExists(source.Entity, f))
                    errors.Add($"SEMANTIC: {where} template token '{{{{{token}}}}}' — '{f}' is not a "
                             + $"field of '{source.Entity}', the entity being iterated");
            }
            else if (!EffectOnlyTokens.Contains(token) && ExprTokens.Describe(token) is { } why)
                errors.Add($"SEMANTIC: {where} template token '{{{{{token}}}}}' is {why}, and 'record.<field>', "
                         + $"{string.Join(", ", EffectOnlyTokens.Select(t => "{{" + t + "}}"))}");
        }
    }

    private static void ValidateCondition(JsonObject cond, string? ctxEntity, string where, BehaviorCtx ctx, List<string> errors)
    {
        var hasAll = cond["all"] is JsonArray;
        var hasAny = cond["any"] is JsonArray;
        var hasLeaf = cond["field"] != null || cond["operator"] != null;
        if ((hasAll ? 1 : 0) + (hasAny ? 1 : 0) + (hasLeaf ? 1 : 0) != 1)
        { errors.Add($"SEMANTIC: {where} condition must be exactly one of: an 'all' array, an 'any' array, or a {{ field, operator }} leaf"); return; }
        if (hasAll || hasAny)
        {
            foreach (var cn in Arr(cond[hasAll ? "all" : "any"]))
                if (cn is JsonObject c) ValidateCondition(c, ctxEntity, where, ctx, errors);
            return;
        }
        var f = Str(cond, "field");
        var path = Str(cond, "path");
        if (f != null && path != null)
            errors.Add($"SEMANTIC: {where} condition gives both 'field' and 'path' — use one");
        else if (f != null)
        {
            if (!ctx.FieldExists(ctxEntity, f))
                errors.Add($"SEMANTIC: {where} condition field '{f}' is not a field of '{ctxEntity}'");
        }
        else if (path != null) ValidateConditionHop(path, ctxEntity, where, ctx, errors);
        else errors.Add($"SEMANTIC: {where} condition leaf needs a 'field' or a 'path'");

        if (Str(cond, "operator") == null)
            errors.Add($"SEMANTIC: {where} condition leaf needs an 'operator'");
        ValidateOperatorShape(cond, k => ctx.FieldExists(ctxEntity, k), ctxEntity, $"{where} condition", errors);
        foreach (var tok in ValueTokens(cond["value"]))
            if (ExprTokens.Describe(tok) is { } why)
                errors.Add($"SEMANTIC: {where} condition value token '{{{{{tok}}}}}' is {why}");
    }

    /// <summary>Every <c>{{…}}</c> token a leaf's value carries — one for a scalar, and each element
    /// of a range/list value, so <c>between ["{{today}}", "{{today+7}}"]</c> is checked end to end.</summary>
    private static IEnumerable<string> ValueTokens(JsonNode? value)
    {
        if (value is JsonArray arr) return arr.SelectMany(ValueTokens);
        if (value is JsonValue v && v.TryGetValue<string>(out var s) && ExprTokens.Inner(s) is { } inner)
            return [inner];
        return [];
    }

    /// <summary>A condition's one-hop <c>path</c>: through a same-app reference, landing on a plain
    /// value. Deliberately the same rules the renderer's filter hop follows, so a guard and a filter
    /// can be written the same way.</summary>
    private static void ValidateConditionHop(string path, string? entity, string where, BehaviorCtx ctx, List<string> errors)
    {
        var dot = path.IndexOf('.');
        if (dot <= 0 || dot == path.Length - 1)
        { errors.Add($"SEMANTIC: {where} condition path '{path}' must be '<reference field>.<field on the target>'"); return; }
        var refKey = path[..dot];
        var rest = path[(dot + 1)..];
        if (entity == null) return;
        if (!ctx.FieldDefs.GetValueOrDefault(entity, new()).TryGetValue(refKey, out var rf))
        { errors.Add($"SEMANTIC: {where} condition path '{path}' hops '{refKey}', which is not a field of '{entity}'"); return; }
        if (Str(rf, "type") != "reference")
        { errors.Add($"SEMANTIC: {where} condition path '{path}' hops '{entity}.{refKey}', which is a '{Str(rf, "type")}' — only a reference can be hopped through"); return; }
        if (Str(rf, "targetApp") != null)
        { errors.Add($"SEMANTIC: {where} condition path '{path}' hops into another app's data, which the engine cannot read — hop a same-app reference"); return; }
        var target = Str(rf, "targetEntity");
        if (target == null || !ctx.Entities.Contains(target))
        { errors.Add($"SEMANTIC: {where} condition path '{path}' hops '{entity}.{refKey}', whose targetEntity '{target}' is unknown"); return; }
        if (!ctx.FieldExists(target, rest))
            errors.Add($"SEMANTIC: {where} condition path '{path}' reads '{rest}', which is not a field of '{target}'");
    }

    /// <summary>Operator-specific shape rules, shared by behavior conditions and block filters.
    /// <c>between</c>/<c>overlaps</c> take a two-element value, and <c>overlaps</c> needs the
    /// <c>endField</c> that completes the row's range — without it there is no range to overlap.</summary>
    private static void ValidateOperatorShape(JsonObject leaf, Func<string, bool> fieldExists,
        string? entity, string where, List<string> errors)
    {
        var op = Str(leaf, "operator");
        var endField = Str(leaf, "endField");

        if (op is "between" or "overlaps")
        {
            // A '{{…}}' string is allowed: a screen-state facet may carry the pair itself.
            var v = leaf["value"];
            var isPair = v is JsonArray { Count: 2 };
            var isToken = v is JsonValue tv && tv.TryGetValue<string>(out var ts) && ExprTokens.Inner(ts) != null;
            if (!isPair && !isToken)
                errors.Add($"SEMANTIC: {where}: operator '{op}' needs a two-element value — "
                         + (op == "between" ? "the inclusive pair [lo, hi]" : "the window [from, to]"));
        }
        if (op == "overlaps" && endField == null)
            errors.Add($"SEMANTIC: {where}: operator 'overlaps' needs an 'endField' — the row's range is [field, endField]");
        if (op != "overlaps" && endField != null)
            errors.Add($"SEMANTIC: {where}: 'endField' only means something with operator 'overlaps' (this is '{op}')");
        if (endField != null && entity != null && !fieldExists(endField))
            errors.Add($"SEMANTIC: {where}: endField '{endField}' is not a field of '{entity}'");
    }

    /// <summary>Field `initial` rules: create-time conditional values that may hop ONE relation via
    /// `path` (the runtime idiom for a cross-record condition — a field's start value depending on a
    /// referenced record, e.g. a booking auto-approving for standard rooms). Each rule is
    /// { when: &lt;leaf guard&gt;, value }; the rule's own value must be legal for the field.</summary>
    private static void ValidateInitialRules(BehaviorCtx ctx, List<string> errors)
    {
        foreach (var (ent, fields) in ctx.FieldDefs)
            foreach (var (fk, fd) in fields)
            {
                if (fd["initial"] is not JsonArray rules) continue;
                var i = 0;
                foreach (var rn in rules)
                {
                    var rw = $"field '{ent}.{fk}' initial[{i++}]";
                    if (rn is not JsonObject rule) continue;
                    // The value the rule sets must be a real option when the field is a select.
                    if (rule["value"] is JsonValue rvv && rvv.TryGetValue<string>(out var rval)
                        && ctx.FieldType(ent, fk) == "select" && !OptionValuesOf(ctx, ent, fk).Contains(rval))
                        errors.Add($"SEMANTIC: {rw} value '{rval}' is not an option of the '{fk}' select");
                    if (rule["when"] is not JsonObject when)
                    { errors.Add($"SEMANTIC: {rw} needs a 'when' guard"); continue; }
                    ValidateLeafGuard(when, ent, rw, ctx, errors);
                }
            }
    }

    private static readonly HashSet<string> ComputedNumericTypes = new() { "integer", "decimal", "money" };
    /// <summary>What a computed field may be typed. <c>date</c> joined the list with the boundary
    /// functions — <c>start_of_week</c> and its siblings answer a date, and there was nowhere to put
    /// one. Deliberately NOT <c>datetime</c>: nothing in the language produces an instant, so allowing
    /// it would advertise a column no expression can fill.</summary>
    private static readonly HashSet<string> ComputedOutputTypes =
        new() { "integer", "decimal", "money", "boolean", "date" };
    // Always-present system datetime columns the compiler stamps — usable as date-diff args (ticket age
    // from created_at) even though they aren't authored fields.
    private static readonly HashSet<string> SystemDateFields = new() { "created_at", "updated_at" };

    /// <summary>What somebody reaches for when they want the current time inside an expression.
    /// Named so the refusal can explain itself: these are absent by design, not missing. They are
    /// legal as <c>{{today}}</c> / <c>{{now}}</c> TOKENS, which resolve where the question is asked
    /// rather than where a row was last saved.</summary>
    private static readonly HashSet<string> ClockWords =
        new(StringComparer.Ordinal) { "today", "now", "current_date", "current_time", "utcnow" };

    /// <summary>What a generated date row carries, and therefore the whole of what
    /// <c>{{source.*}}</c> can name when a <c>createForEach</c> iterates a range rather than an
    /// entity. <c>end</c> is the last day before the next step begins, which is what makes a month
    /// row cover a month instead of a day.</summary>
    private static readonly HashSet<string> RangeRowFields =
        new(StringComparer.Ordinal) { "index", "date", "end" };

    /// <summary>
    /// What <c>{{source.*}}</c> refers to where a template is being checked.
    ///
    /// <para>Null everywhere except inside a <c>createForEach</c>, which is the only effect that
    /// iterates anything — so <c>{{source.whatever}}</c> in a notify or an updateRecord is an author
    /// reaching for a row that is not there, and is now said rather than silently resolved to
    /// nothing at run time.</para>
    /// </summary>
    private sealed record SourceScope(string? Entity, bool IsRange);

    /// <summary>
    /// What <c>{{created.*}}</c> refers to: the entity the most recent <c>createRecord</c> ABOVE
    /// this effect inserts into, or null when nothing has been created yet.
    ///
    /// <para>Position matters, which is why this is threaded down the effect list rather than
    /// gathered from it. An effect naming <c>{{created.id}}</c> when the create comes AFTER it
    /// resolves to nothing at run time and writes a blank reference — a child pointing at no parent,
    /// which reads as missing data rather than as the effects being in the wrong order.</para>
    /// </summary>
    private sealed record CreatedScope(string Entity);

    /// <summary>Computed fields: numeric type, exactly one of expr/rollup, no clash with authored
    /// value sources (default/initial/options/required/role:status). An `expr` must parse and may
    /// read this entity's plain numeric fields and rollup fields — never another expr field, so
    /// evaluation order is always rollups-then-exprs with no chains to order. A `rollup` must point
    /// back at this entity through a real local reference on the aggregated entity, with a numeric
    /// aggregated field for sum/avg/min/max and field-only leaf filters (no path hops — the rows are
    /// already the related records).</summary>
    private static void ValidateComputedFields(BehaviorCtx ctx, List<string> errors)
    {
        // The series declaration, before anything that depends on it. A partition that is not a local
        // reference, or an order that is not sortable, would make "the previous row" arbitrary — and an
        // arbitrary previous row in a running balance is a wrong number nobody can see is wrong.
        foreach (var (ent, entity) in ctx.EntityDefs ?? [])
        {
            if (entity["series"] is not JsonObject series) continue;
            var sw = $"entity '{ent}' series";
            if (Str(series, "partition") is { } part)
            {
                if (!ctx.FieldDefs.TryGetValue(ent, out var pf) || !pf.TryGetValue(part, out var pd)
                    || Str(pd, "type") != "reference" || Str(pd, "targetApp") != null)
                    errors.Add($"SEMANTIC: {sw} partition '{part}' must be a local reference field on '{ent}' — "
                             + "rows sharing it are one series");
            }
            if (Str(series, "order") is { } ord)
            {
                if (!ctx.FieldExists(ent, ord))
                    errors.Add($"SEMANTIC: {sw} order '{ord}' is not a field of '{ent}'");
                else if (ctx.FieldType(ent, ord) is not ("integer" or "decimal" or "money" or "date" or "datetime"))
                    errors.Add($"SEMANTIC: {sw} order '{ord}' is a {ctx.FieldType(ent, ord)}; "
                             + "ordering needs a number or a date");
            }
        }

        foreach (var (ent, fields) in ctx.FieldDefs)
            foreach (var (fk, fd) in fields)
            {
                if (fd["computed"] is not JsonObject comp) continue;
                var cw = $"field '{ent}.{fk}' computed";

                if (!ComputedOutputTypes.Contains(Str(fd, "type") ?? ""))
                    errors.Add($"SEMANTIC: {cw} is only valid on integer/decimal/money/boolean/date fields, not '{Str(fd, "type")}'");
                if (fd["default"] is not null || fd["initial"] is not null)
                    errors.Add($"SEMANTIC: {cw} cannot combine with 'default'/'initial' — the computation IS the value");
                if (fd["options"] is not null)
                    errors.Add($"SEMANTIC: {cw} cannot have 'options'");
                if (fd["required"]?.GetValue<bool>() == true)
                    errors.Add($"SEMANTIC: {cw} cannot be 'required' — the server always fills it");
                if (Str(fd, "role") == "status")
                    errors.Add($"SEMANTIC: {cw} cannot be the role:'status' field");

                var expr = Str(comp, "expr");
                var rollup = comp["rollup"] as JsonObject;
                if ((expr == null) == (rollup == null))
                { errors.Add($"SEMANTIC: {cw} needs exactly one of 'expr' or 'rollup'"); continue; }

                if (expr != null)
                {
                    // `prev()` is only meaningful inside a declared series: without a partition and an
                    // order, "the previous row" names nothing. Refused here rather than silently
                    // reading as the seed forever, which would look like a plan that never accumulates.
                    if (expr.Contains(ComputedExpr.PrevFunc + "(", StringComparison.Ordinal)
                        && ctx.EntityDefs?.TryGetValue(ent, out var prevEnt) == true
                        && prevEnt["series"] is not JsonObject)
                        errors.Add($"SEMANTIC: {cw} expr calls {ComputedExpr.PrevFunc}() but '{ent}' "
                                 + "declares no 'series' — add series.partition and series.order so "
                                 + "'the previous row' has a meaning");

                    // `scenario.price_per_user` — a value read from the record this one references.
                    // Resolved to (entity, field) here so the kind check and the error message below
                    // are the same code for a local field and a hop.
                    (string Entity, string Field)? Resolve(string ident)
                    {
                        if (ComputedExpr.Hop(ident) is not { } hop) return (ent, ident);
                        if (!ctx.FieldDefs.TryGetValue(ent, out var own)
                            || !own.TryGetValue(hop.Reference, out var refDef)
                            || Str(refDef, "type") != "reference" || Str(refDef, "targetApp") != null
                            || Str(refDef, "targetEntity") is not { } target) return null;
                        return (target, hop.Field);
                    }

                    var validation = ComputedExpr.Validate(expr, fieldKind: ident =>
                    {
                        if (Resolve(ident) is not { } at) return null;
                        var type = ctx.FieldType(at.Entity, at.Field);
                        return ComputedNumericTypes.Contains(type ?? "") ? ComputedValueKind.Number
                            : type == "boolean" ? ComputedValueKind.Boolean
                            : type is "date" or "datetime" ? ComputedValueKind.Date
                            : null;
                    }, identError: ident =>
                    {
                        if (ComputedExpr.Hop(ident) is { } hop)
                        {
                            if (Resolve(ident) is not { } at)
                                return $"'{hop.Reference}' is not a local reference field of '{ent}', so "
                                     + $"'{ident}' cannot be read";
                            if (!ctx.FieldExists(at.Entity, at.Field))
                                return $"'{at.Field}' is not a field of '{at.Entity}'";
                            if (!ComputedOutputTypes.Contains(ctx.FieldType(at.Entity, at.Field) ?? "")
                                && ctx.FieldType(at.Entity, at.Field) is not ("date" or "datetime"))
                                return $"'{ident}' is not a numeric, boolean, or date field";
                            // One hop only. Two would mean a join whose cost nothing here can see, and
                            // whose invalidation nothing here can track.
                            if (at.Field.Contains('.'))
                                return $"'{ident}' hops twice; a computed field may follow one reference";
                            return null;
                        }
                        if (ident == fk) return $"'{ident}' is the computed field itself";
                        if (!ctx.FieldExists(ent, ident)) return $"'{ident}' is not a field of '{ent}'";
                        if (!ComputedOutputTypes.Contains(ctx.FieldType(ent, ident) ?? "")
                            && ctx.FieldType(ent, ident) is not ("date" or "datetime"))
                            return $"'{ident}' is not a numeric, boolean, or date field";
                        return null;
                    }, prevArgError: prevArg =>
                    {
                        // prev()'s argument. The self-reference check is deliberately ABSENT: reading
                        // your own field on the previous row is what a running total IS.
                        if (Resolve(prevArg) is not { } at || !ctx.FieldExists(at.Entity, at.Field))
                            return $"'{prevArg}' is not a field of '{ent}'";
                        if (!ComputedNumericTypes.Contains(ctx.FieldType(at.Entity, at.Field) ?? ""))
                            return $"'{prevArg}' is not a numeric field, so it cannot accumulate";
                        return null;
                    }, dateArgError: dateArg =>
                    {
                        // Duration-function argument: an authored date/datetime field, or a system timestamp.
                        if (SystemDateFields.Contains(dateArg)) return null;
                        if (dateArg == fk) return $"'{dateArg}' is the computed field itself";

                        // The clock is absent ON PURPOSE, so say that rather than "not a field" —
                        // which is true, and reads as though the author merely misspelled something
                        // they could go and add. A computed field is worked out when its row is
                        // written and stored beside it, so a figure derived from the current time
                        // would be right on the day it saved and silently wrong every day after.
                        if (ClockWords.Contains(dateArg))
                            return $"'{dateArg}' is not available in a computed field: the figure is "
                                + "worked out when the row is written and stored, so it would stop "
                                + "being true the next day. Compare two stored dates, or ask the "
                                + "question in a filter or a report, which run when somebody looks";

                        if (!ctx.FieldExists(ent, dateArg)) return $"'{dateArg}' is not a field of '{ent}'";
                        var ty = ctx.FieldType(ent, dateArg);
                        if (ty is not ("date" or "datetime"))
                            return $"'{dateArg}' must be a date/datetime field (is '{ty}')";
                        return null;
                    }, datePartArgError: (part, dateArg) =>
                    {
                        // An hour needs a time of day, and a `date` column has none — every row would
                        // answer 0. Refused rather than allowed to be a column of zeros, on the same
                        // reasoning that rejects an hour offset on '{{today}}'.
                        if (part != ComputedExpr.HourFunc) return null;
                        if (SystemDateFields.Contains(dateArg)) return null;
                        return ctx.FieldType(ent, dateArg) == "date"
                            ? $"'{dateArg}' is a date and has no time of day, so '{part}' would be 0 on "
                                + "every row — read an hour from a datetime field"
                            : null;
                    });
                    if (validation.Error != null)
                        errors.Add($"SEMANTIC: {cw} expr — {validation.Error}");
                    else
                    {
                        // A computed field is a number, a boolean, or — since start_of_week and its
                        // siblings — a DATE. The type of the column decides which, so an expression
                        // answering the wrong one is caught here rather than at `dotnet build`.
                        var expected = Str(fd, "type") switch
                        {
                            "boolean" => ComputedValueKind.Boolean,
                            "date" => ComputedValueKind.Date,
                            _ => ComputedValueKind.Number,
                        };
                        if (validation.ResultKind != expected)
                            errors.Add($"SEMANTIC: {cw} expr returns a {ComputedKindName(validation.ResultKind)}, not a {ComputedKindName(expected)}");
                    }
                }

                if (rollup != null)
                {
                    if (!ComputedNumericTypes.Contains(Str(fd, "type") ?? ""))
                        errors.Add($"SEMANTIC: {cw} rollup is only valid on integer/decimal/money fields");
                    var re = Str(rollup, "entity");
                    var via = Str(rollup, "via");
                    var op = Str(rollup, "op");
                    var rf = Str(rollup, "field");
                    if (re == null || !ctx.Entities.Contains(re))
                    { errors.Add($"SEMANTIC: {cw} rollup entity '{re}' is unknown"); continue; }
                    // `match` turns the rollup sideways: the aggregated rows point at a record THIS
                    // record also points at, rather than at this record. So `via` must target whatever
                    // `match` targets — not this entity.
                    var match = Str(rollup, "match");
                    var expectedTarget = ent;
                    if (match != null)
                    {
                        if (!ctx.FieldDefs.TryGetValue(ent, out var own) || !own.TryGetValue(match, out var matchDef)
                            || Str(matchDef, "type") != "reference" || Str(matchDef, "targetApp") != null)
                            errors.Add($"SEMANTIC: {cw} rollup match '{match}' must be a local reference field on '{ent}'");
                        else
                            expectedTarget = Str(matchDef, "targetEntity") ?? ent;
                    }
                    if (via == null || !ctx.FieldDefs.TryGetValue(re, out var refs) || !refs.TryGetValue(via, out var viaDef)
                        || Str(viaDef, "type") != "reference" || Str(viaDef, "targetApp") != null || Str(viaDef, "targetEntity") != expectedTarget)
                        errors.Add(match == null
                            ? $"SEMANTIC: {cw} rollup via '{via}' must be a local reference field on '{re}' pointing at '{ent}'"
                            : $"SEMANTIC: {cw} rollup via '{via}' must be a local reference field on '{re}' pointing at '{expectedTarget}' — the same entity match '{match}' points at, since that is what makes the two records siblings");

                    // A window is only meaningful over dates, and a mistyped field here would silently
                    // aggregate nothing rather than fail — the worst outcome for a plan.
                    if (rollup["window"] is JsonObject win)
                    {
                        // Two directions, and a window must commit to one. 'against' asks whether the
                        // ROW's span covers my date; 'at' asks whether the ROW's date falls in my span.
                        // A window naming both would be read one way and authored the other.
                        var within = win["within"] as JsonObject;
                        var isBucket = Str(win, "at") is not null || within is not null;
                        var isSpan = Str(win, "against") is not null;
                        (string Prop, string? On, string? Field)[] parts;
                        if (isBucket && isSpan)
                        {
                            errors.Add($"SEMANTIC: {cw} rollup window names both 'against' and 'at' — "
                                     + "use 'from'/'to'/'against' for rows that span a date, or "
                                     + "'at'/'within' for rows that land on one");
                            parts = [];
                        }
                        else if (isBucket)
                        {
                            if (Str(win, "at") is null)
                                errors.Add($"SEMANTIC: {cw} rollup window has a 'within' range but no 'at' date to place in it");
                            if (within is null || (Str(within, "from") is null && Str(within, "to") is null))
                                errors.Add($"SEMANTIC: {cw} rollup window 'at' needs a 'within' range on '{ent}' to fall inside; "
                                         + "an unbounded bucket would collect every row");
                            parts = [("at", re, Str(win, "at")),
                                     ("within.from", ent, Str(within, "from")),
                                     ("within.to", ent, Str(within, "to"))];
                        }
                        else
                        {
                            if (!isSpan)
                                errors.Add($"SEMANTIC: {cw} rollup window needs 'against' (a date on '{ent}' the row's range must cover) or 'at' (a date on '{re}' falling inside a range on '{ent}')");
                            if (Str(win, "from") is null && Str(win, "to") is null)
                                errors.Add($"SEMANTIC: {cw} rollup window 'against' needs a 'from' and/or 'to' date on '{re}' to bound the row");
                            parts = [("from", re, Str(win, "from")),
                                     ("to", re, Str(win, "to")),
                                     ("against", ent, Str(win, "against"))];
                        }
                        // A window orders DATES or NUMBERS — "the months this hire covers" and "the
                        // first six periods" are the same question asked over different scales. What
                        // it cannot do is mix them: comparing a sequence to a date is not a narrower
                        // window, it is an empty one, and an empty window aggregates in silence.
                        var kinds = new List<(string Prop, string Kind)>();
                        foreach (var (prop, onEntity, wf) in parts)
                        {
                            if (wf is null) continue;
                            if (!ctx.FieldExists(onEntity, wf))
                            { errors.Add($"SEMANTIC: {cw} rollup window '{prop}' field '{wf}' is not a field of '{onEntity}'"); continue; }
                            var t = ctx.FieldType(onEntity, wf);
                            if (t is "date" or "datetime") kinds.Add((prop, "date"));
                            else if (t is "integer" or "decimal" or "money") kinds.Add((prop, "number"));
                            else
                                errors.Add($"SEMANTIC: {cw} rollup window '{prop}' field '{wf}' is a "
                                         + $"{t}; a window orders dates or numbers");
                        }
                        if (kinds.Select(k => k.Kind).Distinct().Count() > 1)
                            errors.Add($"SEMANTIC: {cw} rollup window mixes dates and numbers ("
                                     + string.Join(", ", kinds.Select(k => $"{k.Prop} is a {k.Kind}"))
                                     + ") — a window has to compare like with like, and one that does "
                                     + "not simply matches nothing");
                    }
                    if (op != "count")
                    {
                        if (rf == null)
                            errors.Add($"SEMANTIC: {cw} rollup op '{op}' needs a 'field' to aggregate");
                        else if (!ctx.FieldExists(re, rf) || !ComputedNumericTypes.Contains(ctx.FieldType(re, rf) ?? ""))
                            errors.Add($"SEMANTIC: {cw} rollup field '{rf}' is not a numeric field of '{re}'");
                    }
                    else if (rf != null)
                        errors.Add($"SEMANTIC: {cw} rollup op 'count' must not name a 'field'");
                    foreach (var fn in (rollup["filters"] as JsonArray ?? new()).OfType<JsonObject>())
                    {
                        var ff = Str(fn, "field");
                        if (ff == null || fn["path"] is not null)
                            errors.Add($"SEMANTIC: {cw} rollup filters must use 'field' (no 'path' hops)");
                        else if (!ctx.FieldExists(re, ff))
                            errors.Add($"SEMANTIC: {cw} rollup filter field '{ff}' is not a field of '{re}'");
                        if (Str(fn, "operator") == null)
                            errors.Add($"SEMANTIC: {cw} rollup filter needs an 'operator'");
                    }
                }
            }

        foreach (var (ent, fields) in ctx.FieldDefs)
            ValidateComputedCycles(ent, fields, errors);
    }

    private static string ComputedKindName(ComputedValueKind? kind) =>
        kind switch
        {
            ComputedValueKind.Boolean => "boolean",
            ComputedValueKind.Date => "date",
            _ => "number",
        };

    private static void ValidateComputedCycles(string entity,
        IReadOnlyDictionary<string, JsonObject> fields, List<string> errors)
    {
        var expressions = fields
            .Where(pair => pair.Value["computed"]?["expr"] is JsonValue)
            .ToDictionary(pair => pair.Key,
                pair => Str((JsonObject)pair.Value["computed"]!, "expr") ?? "",
                StringComparer.Ordinal);
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        var stack = new List<string>();
        var reported = new HashSet<string>(StringComparer.Ordinal);

        void Visit(string key)
        {
            if (state.GetValueOrDefault(key) == 2) return;
            if (state.GetValueOrDefault(key) == 1)
            {
                var start = stack.IndexOf(key);
                var cycle = stack.Skip(Math.Max(0, start)).Append(key).ToArray();
                var signature = string.Join("->", cycle);
                if (reported.Add(signature))
                    errors.Add($"SEMANTIC: entity '{entity}' has a computed expression cycle: {string.Join(" -> ", cycle)}");
                return;
            }

            state[key] = 1;
            stack.Add(key);
            // LOCAL identifiers: a prev() reference points at the PREVIOUS row, so it can never close
            // a cycle. `active_tenants = prev(active_tenants) + new - churned` is the ordinary shape of
            // a recurrence, not a self-reference, and reporting it as one would forbid the primitive.
            foreach (var dependency in ComputedExpr.LocalIdentifiers(expressions[key]))
                if (expressions.ContainsKey(dependency)) Visit(dependency);
            stack.RemoveAt(stack.Count - 1);
            state[key] = 2;
        }

        foreach (var key in expressions.Keys) Visit(key);
    }

    /// <summary>Validate a { field | path, operator, value } leaf guard (the `filter` shape) against an
    /// entity: exactly one of field/path, the operator is present, a `field` resolves on the entity, and a
    /// one-hop `path` '&lt;referenceField&gt;.&lt;targetField&gt;' points through a real LOCAL reference to a
    /// real field on the target entity (cross-app/platform targets aren't introspected here).</summary>
    private static void ValidateLeafGuard(JsonObject when, string ent, string where, BehaviorCtx ctx, List<string> errors)
    {
        var field = Str(when, "field");
        var path = Str(when, "path");
        if ((field == null) == (path == null))
        { errors.Add($"SEMANTIC: {where} guard needs exactly one of 'field' or 'path'"); return; }
        if (Str(when, "operator") == null)
            errors.Add($"SEMANTIC: {where} guard needs an 'operator'");
        if (field != null && !ctx.FieldExists(ent, field))
            errors.Add($"SEMANTIC: {where} guard field '{field}' is not a field of '{ent}'");
        if (path != null)
        {
            var dot = path.IndexOf('.');
            if (dot <= 0 || dot == path.Length - 1)
            { errors.Add($"SEMANTIC: {where} guard path '{path}' must be '<referenceField>.<targetField>'"); return; }
            var refField = path[..dot];
            var target = path[(dot + 1)..];
            if (!ctx.FieldExists(ent, refField) || ctx.FieldType(ent, refField) != "reference")
                errors.Add($"SEMANTIC: {where} guard path '{path}' — '{refField}' is not a reference field on '{ent}'");
            else if (ctx.FieldDefs.TryGetValue(ent, out var efs) && efs.TryGetValue(refField, out var rfd)
                     && Str(rfd, "targetApp") == null   // only introspect same-app targets
                     && TargetEntityOf(ctx, ent, refField) is { } te && !ctx.FieldExists(te, target))
                errors.Add($"SEMANTIC: {where} guard path '{path}' — '{target}' is not a field of '{te}'");
        }
    }

    /// <summary>Shared lookups for block-tree validation.</summary>
    private sealed record BlockCtx(
        HashSet<string> Entities,
        HashSet<string> ViewKeys,
        Func<string?, string?, bool> FieldExists,
        Func<string?, string?, string?> FieldType,
        Dictionary<string, Dictionary<string, JsonObject>> FieldDefs,
        Dictionary<string, Dictionary<string, JsonObject>> CommandsByEntity,
        Dictionary<string, string?> PageEntity,
        Dictionary<string, string> EntityRole,
        Dictionary<string, string> EntityKind)
    {
        /// <summary>True when the entity holds many user-created records — the default when `kind`
        /// is absent. A `config`/`settings` entity is a singleton the app reads, so "New one of
        /// these" is meaningless on it.</summary>
        public bool IsCollection(string? entity) =>
            entity != null && EntityKind.GetValueOrDefault(entity, "collection") == "collection";

        /// <summary>The entities playing an archetype role (formTemplate, formResponse, …).</summary>
        public List<string> WithRole(string role) =>
            EntityRole.Where(kv => kv.Value == role).Select(kv => kv.Key).ToList();

        /// <summary>
        /// `externalEmbed` keys already seen, per bound entity.
        ///
        /// <para>Lives on the context rather than in `ValidateBlocks` because that method recurses —
        /// two embeds with one key would sit in different tabs, different columns and different
        /// recursion frames, which is exactly the arrangement a per-call local would miss. The embed
        /// route addresses a block BY key, so a duplicate makes the route ambiguous.</para>
        /// </summary>
        public Dictionary<string, HashSet<string>> EmbedKeys { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>One rule for every surface that takes a filterBar (repeat grids, table blocks, child
    /// tables, table-view configs): search fields must exist on the entity; facet fields must exist
    /// AND be discrete (select/multiselect/reference) — a free-text facet dropdown is meaningless.
    /// No-ops when the entity is unresolvable (a bucket-axis repeat) — the renderer gates there.</summary>
    private static void ValidateFilterBar(JsonObject? fb, string? entity, string where, BlockCtx ctx, List<string> errors)
    {
        if (fb == null || entity == null) return;
        foreach (var fn in Arr(fb["search"]))
            if (fn?.GetValue<string>() is { } fk && !ctx.FieldExists(entity, fk))
                errors.Add($"SEMANTIC: {where}: filterBar search field '{fk}' is not a field of '{entity}'");
        foreach (var fn in Arr(fb["facets"]))
        {
            if (fn?.GetValue<string>() is not { } fk) continue;
            if (!ctx.FieldExists(entity, fk))
            { errors.Add($"SEMANTIC: {where}: filterBar facet '{fk}' is not a field of '{entity}'"); continue; }
            if (ctx.FieldType(entity, fk) is { } ft && ft is not ("select" or "multiselect" or "reference"))
                errors.Add($"SEMANTIC: {where}: filterBar facet '{fk}' must be a select/multiselect/reference field on '{entity}' (is '{ft}')");
        }
    }

    /// <summary>One rule for every surface that groups rows (child tables, table blocks): the group
    /// field must be a 'select' (groups = its options) or a LOCAL 'reference' (groups = the referenced
    /// records) on the rows' entity; `orderBy` is only meaningful for a reference and must resolve on
    /// the referenced entity. No-ops when the entity is unresolvable.</summary>
    private static void ValidateGroupBy(JsonObject? gb, string? entity, string where, BlockCtx ctx, List<string> errors)
    {
        if (gb == null || entity == null) return;
        var gf = Str(gb, "field");
        if (gf == null || !ctx.FieldDefs.GetValueOrDefault(entity, new()).TryGetValue(gf, out var gfd))
        { errors.Add($"SEMANTIC: {where}: groupBy field '{gf}' is not a field of '{entity}'"); return; }
        var gft = Str(gfd, "type");
        var isLocalRef = gft == "reference" && Str(gfd, "targetApp") == null;
        if (gft != "select" && !isLocalRef)
            errors.Add($"SEMANTIC: {where}: groupBy field '{entity}.{gf}' must be a 'select' or a local 'reference' — groups need discrete, resolvable buckets");
        if (Str(gb, "orderBy") is { } ob)
        {
            if (!isLocalRef)
                errors.Add($"SEMANTIC: {where}: groupBy orderBy is only valid when the field is a reference (a select's options are already ordered)");
            else if (Str(gfd, "targetEntity") is { } te && !ctx.FieldExists(te, ob))
                errors.Add($"SEMANTIC: {where}: groupBy orderBy '{ob}' is not a field of '{te}'");
        }
    }

    /// <summary>`orderField` (manual row order on child/table surfaces) must be an integer/decimal
    /// field of the rows' entity — order is a number the client midpoints, nothing else works.</summary>
    private static void ValidateOrderField(string? of, string? entity, string where, BlockCtx ctx, List<string> errors)
    {
        if (of == null || entity == null) return;
        if (!ctx.FieldExists(entity, of))
            errors.Add($"SEMANTIC: {where}: orderField '{of}' is not a field of '{entity}'");
        else if (ctx.FieldType(entity, of) is { } ot && ot is not ("integer" or "decimal"))
            errors.Add($"SEMANTIC: {where}: orderField '{entity}.{of}' must be an 'integer' or 'decimal' field (is '{ot}')");
    }

    /// <summary>A KPI/chart's click-through: the target page must exist, and its filters must be
    /// plain field leaves that resolve on the target page's entity (when the page declares one).</summary>
    private static void ValidateDeepLink(JsonObject link, string where, BlockCtx ctx, List<string> errors)
    {
        var pg = Str(link, "page");
        if (pg == null || !ctx.PageEntity.TryGetValue(pg, out var te))
        { errors.Add($"SEMANTIC: {where}: link page '{pg}' is not a page of this app"); return; }
        foreach (var fn in (link["filters"] as JsonArray ?? new()).OfType<JsonObject>())
        {
            var ff = Str(fn, "field");
            if (ff == null || fn["path"] is not null)
            { errors.Add($"SEMANTIC: {where}: link filters must use 'field' (no 'path' hops)"); continue; }
            if (Str(fn, "operator") == null)
                errors.Add($"SEMANTIC: {where}: link filter '{ff}' needs an 'operator'");
            if (te != null && !ctx.FieldExists(te, ff))
                errors.Add($"SEMANTIC: {where}: link filter field '{ff}' is not a field of '{te}' (page '{pg}')");
        }
    }

    /// <summary>Every command placed on the `recordHeader` is reachable from its entity's hub.
    ///
    /// The hub draws the keys in its `actions` array and nothing else, so `placements` on its own
    /// renders no button. An entity with commands but NO hub is fine (its detail is composed some
    /// other way, and the row/bulk placements still work); the error is a hub that exists and omits
    /// a command that asked to be on the header.
    ///
    /// A TRANSITION-BOUND command is exempt: the process stepper and the inline status cell offer the
    /// moves leaving the record's current state, so it has a real surface whether or not the hub
    /// repeats it. Without that exemption this rule would fire on a perfectly reachable command.</summary>
    private static void ValidateRecordHeaderCommands(JsonArray entities,
        Dictionary<string, Dictionary<string, JsonObject>> commandsByEntity,
        HashSet<string> transitionBoundCommands, List<string> errors)
    {
        foreach (var en in entities)
        {
            if (en is not JsonObject ent || Str(ent, "key") is not { } ekey) continue;
            if (!commandsByEntity.TryGetValue(ekey, out var cmds) || cmds.Count == 0) continue;
            if (ent["detail"] is not JsonObject det) continue;

            var hubActions = CollectHubActions(det["blocks"]);
            if (hubActions is null) continue;   // no hub on this detail — nothing claims to be the header

            foreach (var (ckey, cmd) in cmds)
            {
                if (transitionBoundCommands.Contains(ekey + "|" + ckey)) continue;
                var placements = Arr(cmd["placements"]).Select(p => p?.GetValue<string>()).ToList();
                if (placements.Count == 0) placements.Add("recordHeader");   // the renderer's default
                if (placements.Contains("recordHeader") && !hubActions.Contains(ckey))
                    errors.Add($"SEMANTIC: command '{ckey}' is placed on the 'recordHeader' but is not in "
                             + $"the hub actions of entity '{ekey}' — add '{ckey}' to that hub's 'actions' "
                             + "or drop 'recordHeader' from its placements; the hub renders only what "
                             + "'actions' lists, so as written the button never appears");
            }
        }
    }

    /// <summary>The union of every hub's `actions` in a detail tree, or null when it holds no hub.
    /// A hub with no `actions` gets the renderer's default of edit+delete.</summary>
    private static HashSet<string>? CollectHubActions(JsonNode? blocks)
    {
        HashSet<string>? found = null;
        void Walk(JsonNode? bs)
        {
            foreach (var bn in Arr(bs))
            {
                if (bn is not JsonObject b) continue;
                if (Str(b, "kind") == "hub")
                {
                    found ??= new HashSet<string>(StringComparer.Ordinal);
                    var acts = Arr(b["actions"]).Select(a => a?.GetValue<string>()).Where(a => a is not null).ToList();
                    foreach (var a in acts.Count > 0 ? acts : ["edit", "delete"]) found.Add(a!);
                }
                Walk(b["blocks"]);
                foreach (var tn in Arr(b["tabs"])) if (tn is JsonObject t) Walk(t["blocks"]);
                foreach (var cn in Arr(b["columns"])) Walk(cn);
            }
        }
        Walk(blocks);
        return found;
    }

    /// <summary>Validates a block tree under a binding mode. Collection (pages): view/widgets +
    /// composition kinds. Record (entity.detail): fields/child + composition kinds. Every
    /// reference (view key, field key, child entity/via) must resolve.</summary>
    private static void ValidateBlocks(JsonNode? blocks, string where, string binding, string? boundEntity,
        BlockCtx ctx, List<string> errors)
    {
        foreach (var bn in Arr(blocks))
        {
            if (bn is not JsonObject b) continue;
            switch (Str(b, "kind"))
            {
                case "view":
                    if (binding != "collection")
                    { errors.Add($"SEMANTIC: {where}: block kind 'view' is only valid on pages, not in a record detail"); break; }
                    var vk = Str(b, "view");
                    if (vk == null || !ctx.ViewKeys.Contains(vk))
                        errors.Add($"SEMANTIC: {where} references unknown view '{vk}'");
                    break;
                case "widgets":
                    if (binding != "collection")
                        errors.Add($"SEMANTIC: {where}: block kind 'widgets' is only valid on pages, not in a record detail");
                    // widget bodies are validated by the design layer (needs the catalog configSchemas)
                    break;
                case "settings":
                {
                    if (binding != "collection")
                    { errors.Add($"SEMANTIC: {where}: block kind 'settings' is only valid on pages"); break; }
                    var sente = Str(b, "entity");
                    if (sente == null || !ctx.Entities.Contains(sente))
                        errors.Add($"SEMANTIC: {where}: settings block references unknown entity '{sente}'");
                    break;
                }
                case "fields":
                    if (binding != "record")
                    { errors.Add($"SEMANTIC: {where}: block kind 'fields' is only valid in a record detail"); break; }
                    foreach (var fk in Arr(b["fields"]))
                        if (fk?.GetValue<string>() is { } key && !ctx.FieldExists(boundEntity, key))
                            errors.Add($"SEMANTIC: {where}: fields block references unknown field '{key}' on entity '{boundEntity}'");
                    break;
                case "child":
                {
                    if (binding != "record")
                    { errors.Add($"SEMANTIC: {where}: block kind 'child' is only valid in a record detail"); break; }
                    var ce = Str(b, "entity");
                    var via = Str(b, "via");
                    if (ce == null || !ctx.Entities.Contains(ce))
                    { errors.Add($"SEMANTIC: {where}: child block references unknown entity '{ce}'"); break; }
                    if (via == null || !ctx.FieldDefs.GetValueOrDefault(ce, new()).TryGetValue(via, out var vf))
                        errors.Add($"SEMANTIC: {where}: child block via '{via}' is not a field of '{ce}'");
                    else if (Str(vf, "type") != "reference" || Str(vf, "targetEntity") != boundEntity)
                        errors.Add($"SEMANTIC: {where}: child block via '{ce}.{via}' must be a reference to '{boundEntity}'");
                    foreach (var f in Arr(b["fields"]))
                        if (f?.GetValue<string>() is { } fk && !ctx.FieldExists(ce, fk))
                            errors.Add($"SEMANTIC: {where}: child column '{fk}' is not a field of '{ce}'");
                    ValidateFilterBar(b["filterBar"] as JsonObject, ce, $"{where}: child", ctx, errors);
                    ValidateGroupBy(b["groupBy"] as JsonObject, ce, $"{where}: child", ctx, errors);
                    ValidateOrderField(Str(b, "orderField"), ce, $"{where}: child", ctx, errors);
                    break;
                }
                case "hub":
                {
                    if (binding != "record")
                    { errors.Add($"SEMANTIC: {where}: block kind 'hub' is only valid in a record detail"); break; }
                    foreach (var prop in new[] { "title", "status" })
                        if (Str(b, prop) is { } hk && !ctx.FieldExists(boundEntity, hk))
                            errors.Add($"SEMANTIC: {where}: hub {prop} '{hk}' is not a field of '{boundEntity}'");
                    if (Str(b, "status") is { } sk && ctx.FieldType(boundEntity, sk) is { } st && st != "select")
                        errors.Add($"SEMANTIC: {where}: hub status '{sk}' must be a 'select' field (is '{st}')");
                    foreach (var prop in new[] { "subtitle", "facts" })
                        foreach (var fkn in Arr(b[prop]))
                            if (fkn?.GetValue<string>() is { } hk && !ctx.FieldExists(boundEntity, hk))
                                errors.Add($"SEMANTIC: {where}: hub {prop} field '{hk}' is not a field of '{boundEntity}'");
                    if (b["avatar"] is JsonObject av && Str(av, "field") is { } ak && !ctx.FieldExists(boundEntity, ak))
                        errors.Add($"SEMANTIC: {where}: hub avatar field '{ak}' is not a field of '{boundEntity}'");
                    // hub actions are the built-ins edit/delete or a command defined on the bound entity.
                    foreach (var an in Arr(b["actions"]))
                        if (an?.GetValue<string>() is { } act && act is not ("edit" or "delete")
                            && !(ctx.CommandsByEntity.TryGetValue(boundEntity ?? "", out var hcmds) && hcmds.ContainsKey(act)))
                            errors.Add($"SEMANTIC: {where}: hub action '{act}' is not 'edit', 'delete', or a command on '{boundEntity}'");
                    break;
                }
                case "process":
                {
                    if (binding != "record")
                        errors.Add($"SEMANTIC: {where}: block kind 'process' is only valid in a record detail");
                    // The process stepper derives entirely from the entity's process; nothing to resolve here.
                    break;
                }
                case "history":
                {
                    if (binding != "record")
                        errors.Add($"SEMANTIC: {where}: block kind 'history' is only valid in a record detail — "
                                 + "it is one record's activity feed");
                    // Everything it renders comes from the runtime's own history store; nothing to resolve.
                    break;
                }
                case "externalEmbed":
                {
                    // Record binding only: the embed's subject is derived from the record, so on a
                    // collection there is nothing to derive it from.
                    if (binding != "record")
                    {
                        errors.Add($"SEMANTIC: {where}: block kind 'externalEmbed' is only valid in a record "
                                 + "detail — its subject comes from the record");
                        break;
                    }
                    // The KEY is how the endpoint addresses this block. Blocks are otherwise identified
                    // by their position in the tree, and a position is not a stable API identity —
                    // reordering a tab would silently repoint a live panel at a different provider.
                    // Uniqueness is per ENTITY, not per detail, because the route is
                    // /{entity}/{recordId}/{blockKey} and a duplicate makes it ambiguous.
                    if (Str(b, "key") is not { Length: > 0 } ekey)
                    { errors.Add($"SEMANTIC: {where}: externalEmbed needs a 'key'"); break; }
                    if (!ctx.EmbedKeys.TryGetValue(boundEntity ?? "", out var seen))
                        ctx.EmbedKeys[boundEntity ?? ""] = seen = new HashSet<string>(StringComparer.Ordinal);
                    if (!seen.Add(ekey))
                        errors.Add($"SEMANTIC: {where}: two externalEmbed blocks on '{boundEntity}' share the "
                                 + $"key '{ekey}' — the embed route addresses a block by key, so it must be "
                                 + "unique for the entity");
                    break;
                }
                case "relatedApps":
                {
                    // Record binding only, and for a blunter reason than the embed's: the whole query
                    // is "which apps point at THIS row". On a collection there is no row to point at,
                    // and the panel would have nothing to ask about rather than merely rendering oddly.
                    if (binding != "record")
                        errors.Add($"SEMANTIC: {where}: block kind 'relatedApps' is only valid in a record "
                                 + "detail — it answers what other apps hold about one record");
                    break;
                }
                case "answers":
                {
                    // The record's own submission. `via` is the reference on THIS entity pointing at the
                    // formResponse — the one link that turns "a ticket" into "the ticket this form filed".
                    if (binding != "record")
                    { errors.Add($"SEMANTIC: {where}: block kind 'answers' is only valid in a record detail"); break; }
                    var responses = ctx.WithRole("formResponse");
                    if (responses.Count == 0)
                    { errors.Add($"SEMANTIC: {where}: block kind 'answers' needs an entity with role 'formResponse' — there are no submissions to show"); break; }
                    var fields = ctx.FieldDefs.GetValueOrDefault(boundEntity ?? "", new());
                    var links = fields.Where(kv => Str(kv.Value, "type") == "reference"
                                                && Str(kv.Value, "targetApp") is null
                                                && responses.Contains(Str(kv.Value, "targetEntity") ?? "")).ToList();
                    if (Str(b, "via") is { } avia)
                    {
                        if (!links.Any(kv => kv.Key == avia))
                            errors.Add($"SEMANTIC: {where}: answers block via '{boundEntity}.{avia}' must be a reference to the formResponse entity");
                    }
                    else if (links.Count == 0)
                        errors.Add($"SEMANTIC: {where}: answers block needs '{boundEntity}' to have a reference to the formResponse entity "
                                 + "(that link is what a submission writes when it files the record)");
                    else if (links.Count > 1)
                        errors.Add($"SEMANTIC: {where}: '{boundEntity}' has more than one reference to the formResponse entity — name one with 'via'");
                    break;
                }
                case "intake":
                {
                    if (binding != "collection")
                    { errors.Add($"SEMANTIC: {where}: block kind 'intake' is only valid on pages — it is the list of forms to pick from"); break; }
                    var templates = ctx.WithRole("formTemplate");
                    var ient = Str(b, "entity");
                    if (ient is not null)
                    {
                        if (!ctx.Entities.Contains(ient))
                            errors.Add($"SEMANTIC: {where}: intake block references unknown entity '{ient}'");
                        else if (!templates.Contains(ient))
                            errors.Add($"SEMANTIC: {where}: intake block entity '{ient}' must have role 'formTemplate'");
                    }
                    else if (templates.Count == 0)
                        errors.Add($"SEMANTIC: {where}: block kind 'intake' needs an entity with role 'formTemplate' — there are no forms to offer");
                    else if (templates.Count > 1)
                        errors.Add($"SEMANTIC: {where}: the app has more than one formTemplate entity — name one with 'entity'");
                    foreach (var fn in Arr(b["filters"]))
                        if (fn is JsonObject fo)
                            ValidateFilterAddress(fo, ient ?? templates.FirstOrDefault(), $"{where}: intake filter", ctx, errors);
                    break;
                }
                case "tiles":
                    ValidateTiles(b["tiles"], where, binding, boundEntity, ctx, errors);
                    break;
                case "tabs":
                    foreach (var tn in Arr(b["tabs"]))
                        if (tn is JsonObject t) ValidateBlocks(t["blocks"], where, binding, boundEntity, ctx, errors);
                    break;
                case "section":
                    ValidateBlocks(b["blocks"], where, binding, boundEntity, ctx, errors);
                    break;
                case "columns":
                    foreach (var cn in Arr(b["columns"]))
                        ValidateBlocks(cn, where, binding, boundEntity, ctx, errors);
                    break;

                // ---- composable primitives (M2): generic controls the AI arranges into distinctive
                // surfaces. Layout primitives recurse; leaves read the current row/record. `repeat`
                // establishes a new "item" binding over its source entity for its children. ----
                case "stack" or "row" or "grid" or "card":
                    // `tint` reads a select field on the current row/record to wash the box in its option
                    // color — validated best-effort (a bucket-axis item has no entity to resolve against).
                    if (Str(b, "tint") is { } tintPath)
                        ValidateFieldPath(tintPath, boundEntity, $"{where}: '{Str(b, "kind")}' tint", ctx, errors);
                    ValidateBlocks(b["blocks"], where, binding, boundEntity, ctx, errors);
                    break;
                case "repeat":
                {
                    var rsrc = b["source"] as JsonObject;
                    // A repeat iterates ONE origin. An entity yields records; a date range / a select's
                    // options / the platform directory yield plain buckets that belong to no table. The
                    // non-record origins are what make a second axis expressible — a board's columns are
                    // Mon..Sun, so an entity-only repeat can compose lists but never a grid.
                    var re = ValidateBlockSource(rsrc, $"{where}: repeat", binding, boundEntity, ctx, errors);
                    ValidateFilterBar(b["filterBar"] as JsonObject, re, $"{where}: repeat", ctx, errors);
                    // Only an entity origin binds its children to a record of a known entity; a bucket
                    // axis binds them to an item whose shape the manifest cannot name, so leaves under it
                    // are unresolvable against any entity (validated best-effort, like 'text').
                    ValidateBlocks(b["blocks"], $"{where} repeat", "item", re, ctx, errors);
                    break;
                }
                // A CELL is the one record identified by `keys` — normally the grid axes the cell sits at.
                // It may not exist yet: that is the point. Editing an empty cell CREATES it with the keys
                // filled in, which is what turns a matrix from a report into the actual job (rate every
                // competency for this person) instead of "New…" and re-picking what the grid already knows.
                case "cell":
                {
                    var ce2 = Str(b, "entity");
                    if (ce2 == null || !ctx.Entities.Contains(ce2))
                    { errors.Add($"SEMANTIC: {where}: cell entity '{ce2}' is unknown"); break; }

                    if (b["keys"] is not JsonObject keys || keys.Count == 0)
                    { errors.Add($"SEMANTIC: {where}: cell needs 'keys' identifying the record (e.g. {{\"person\":\"{{{{row.id}}}}\"}})"); break; }
                    foreach (var (kk, _) in keys)
                        if (!ctx.FieldExists(ce2, kk))
                            errors.Add($"SEMANTIC: {where}: cell key '{kk}' is not a field of '{ce2}'");

                    var cf = Str(b, "field");
                    if (cf == null) { errors.Add($"SEMANTIC: {where}: cell needs a 'field' to show"); break; }
                    if (!ctx.FieldExists(ce2, cf))
                    { errors.Add($"SEMANTIC: {where}: cell field '{cf}' is not a field of '{ce2}'"); break; }
                    if (keys.ContainsKey(cf))
                        errors.Add($"SEMANTIC: {where}: cell field '{cf}' is also one of its keys — a cell cannot show the value that identifies it");

                    // An editable cell writes straight through, so the field must be one the runtime would
                    // accept. Only the BASE fields are knowable here: system/readOnly/auto/governedBy are
                    // stamped by the compiler, which downgrades `editable` itself — the same
                    // derive-don't-trust rule boards use (AppCompiler.ResolveCellEditing).
                    if (b["editable"]?.GetValue<bool>() == true && BaseFields.Contains(cf))
                        errors.Add($"SEMANTIC: {where}: cell field '{ce2}.{cf}' is a runtime-owned base field and cannot be 'editable'");
                    break;
                }
                // A TIMELINE is a scheduling/gantt surface: records grouped into lanes by `rowBy`, each
                // drawn as a BAR spanning startField..endField across a shared date axis. Multi-day spans
                // are the point — a grid of single-day cells can't render a 3-day absence as one bar.
                case "timeline":
                {
                    var te = Str(b, "entity");
                    if (te == null || !ctx.Entities.Contains(te))
                    { errors.Add($"SEMANTIC: {where}: timeline entity '{te}' is unknown"); break; }
                    if (Str(b, "rowBy") is not { } rowBy || !ctx.FieldExists(te, rowBy) || ctx.FieldType(te, rowBy) != "reference")
                        errors.Add($"SEMANTIC: {where}: timeline rowBy '{Str(b, "rowBy")}' must be a reference field on '{te}'");
                    if (Str(b, "startField") is not { } sf2 || ctx.FieldType(te, sf2) is not ("date" or "datetime"))
                        errors.Add($"SEMANTIC: {where}: timeline startField '{Str(b, "startField")}' must be a date field on '{te}'");
                    if (Str(b, "endField") is { } ef2 && ctx.FieldType(te, ef2) is not ("date" or "datetime"))
                        errors.Add($"SEMANTIC: {where}: timeline endField '{ef2}' must be a date field on '{te}'");
                    if (Str(b, "colorField") is { } cfld && ctx.FieldType(te, cfld) != "select")
                        errors.Add($"SEMANTIC: {where}: timeline colorField '{cfld}' must be a select field on '{te}'");
                    if (Str(b, "labelField") is { } lfld && !ctx.FieldExists(te, lfld))
                        errors.Add($"SEMANTIC: {where}: timeline labelField '{lfld}' is not a field of '{te}'");
                    break;
                }
                // An ORG CHART is a reporting tree: each record's parent is the record whose `identity`
                // equals its `manager` reference (an employee's "reports to" is a person id, matched to
                // the manager-employee's person id), or — for a self-referential manager — by record id.
                case "orgchart":
                {
                    var oe = Str(b, "entity");
                    if (oe == null || !ctx.Entities.Contains(oe))
                    { errors.Add($"SEMANTIC: {where}: orgchart entity '{oe}' is unknown"); break; }
                    if (Str(b, "manager") is not { } mgr || ctx.FieldType(oe, mgr) != "reference")
                        errors.Add($"SEMANTIC: {where}: orgchart manager '{Str(b, "manager")}' must be a reference field on '{oe}'");
                    if (Str(b, "identity") is { } oidf && !ctx.FieldExists(oe, oidf))
                        errors.Add($"SEMANTIC: {where}: orgchart identity '{oidf}' is not a field of '{oe}'");
                    if (Str(b, "subtitle") is { } osf && !ctx.FieldExists(oe, osf))
                        errors.Add($"SEMANTIC: {where}: orgchart subtitle '{osf}' is not a field of '{oe}'");
                    break;
                }
                // A TABLE renders a source's records directly — the common case that previously
                // needed a named view plus a `view` block to embed it. ENTITY origin only: a
                // dates/options/platform axis yields buckets, which have no columns to tabulate.
                case "table":
                {
                    var tsrc = b["source"] as JsonObject;
                    var tse = ValidateBlockSource(tsrc, $"{where}: table", binding, boundEntity, ctx, errors);
                    if (tsrc != null && tse == null)
                        errors.Add($"SEMANTIC: {where}: table needs an ENTITY source — a dates/options/platform axis has no records to tabulate");
                    if (tse != null)
                        foreach (var f in Arr(b["fields"]))
                            if (f?.GetValue<string>() is { } fk && !ctx.FieldExists(tse, fk))
                                errors.Add($"SEMANTIC: {where}: table column '{fk}' is not a field of '{tse}'");
                    ValidateFilterBar(b["filterBar"] as JsonObject, tse, $"{where}: table", ctx, errors);
                    ValidateGroupBy(b["groupBy"] as JsonObject, tse, $"{where}: table", ctx, errors);
                    ValidateOrderField(Str(b, "orderField"), tse, $"{where}: table", ctx, errors);
                    break;
                }
                // A CALENDAR places a source's records on a month grid by their date field.
                case "calendar":
                {
                    var csrc = b["source"] as JsonObject;
                    var cse = ValidateBlockSource(csrc, $"{where}: calendar", binding, boundEntity, ctx, errors);
                    if (csrc != null && cse == null)
                    { errors.Add($"SEMANTIC: {where}: calendar needs an ENTITY source — only records carry dates to place"); break; }
                    if (cse == null) break;
                    if (Str(b, "startField") is not { } csf || ctx.FieldType(cse, csf) is not ("date" or "datetime"))
                        errors.Add($"SEMANTIC: {where}: calendar startField '{Str(b, "startField")}' must be a date field on '{cse}'");
                    if (Str(b, "labelField") is { } clf && !ctx.FieldExists(cse, clf))
                        errors.Add($"SEMANTIC: {where}: calendar labelField '{clf}' is not a field of '{cse}'");
                    if (Str(b, "colorField") is { } ccf && ctx.FieldType(cse, ccf) != "select")
                        errors.Add($"SEMANTIC: {where}: calendar colorField '{ccf}' must be a select field on '{cse}'");
                    // endField is what turns a dot into a duration. Same type rule as the start;
                    // mirrors the timeline's, so the two surfaces mean the same thing by "ends".
                    if (Str(b, "endField") is { } cef && ctx.FieldType(cse, cef) is not ("date" or "datetime"))
                        errors.Add($"SEMANTIC: {where}: calendar endField '{cef}' must be a date field on '{cse}'");
                    // A day view rules an HOUR axis: on a plain 'date' there is no time of day to
                    // place anything at, so everything would pile onto hour zero and the surface
                    // would read as broken rather than as misconfigured.
                    if (Str(b, "range") == "day" && Str(b, "startField") is { } dsf
                        && ctx.FieldType(cse, dsf) is { } dt && dt != "datetime")
                        errors.Add($"SEMANTIC: {where}: calendar range 'day' needs a 'datetime' startField — '{dsf}' is a '{dt}', which carries no time of day");
                    if (b["timeAxis"] is JsonObject ax)
                    {
                        var from = ax["startHour"]?.GetValue<int>() ?? 7;
                        var to = ax["endHour"]?.GetValue<int>() ?? 21;
                        if (to <= from)
                            errors.Add($"SEMANTIC: {where}: calendar timeAxis endHour ({to}) must be after startHour ({from}) — an axis that ends before it begins has no height to draw into");
                    }
                    break;
                }
                // A BOARD is a kanban: a source's records grouped into columns by a select/reference
                // field (a status field → a process board where a drag runs the transition; a reference
                // → an assignment board where a drag reassigns). ENTITY origin only.
                case "board":
                {
                    var bsrc = b["source"] as JsonObject;
                    var bse = ValidateBlockSource(bsrc, $"{where}: board", binding, boundEntity, ctx, errors);
                    if (bsrc != null && bse == null)
                    { errors.Add($"SEMANTIC: {where}: board needs an ENTITY source — a dates/options/platform axis has no records to place in columns"); break; }
                    if (bse == null) break;
                    if (Str(b, "groupField") is not { } gf)
                        errors.Add($"SEMANTIC: {where}: board needs a 'groupField'");
                    else if (ctx.FieldType(bse, gf) is not ("select" or "reference"))
                        errors.Add($"SEMANTIC: {where}: board groupField '{gf}' must be a select or reference field on '{bse}'");
                    foreach (var f in Arr(b["cardFields"]))
                        if (f?.GetValue<string>() is { } fk && !ctx.FieldExists(bse, fk))
                            errors.Add($"SEMANTIC: {where}: board cardField '{fk}' is not a field of '{bse}'");
                    break;
                }
                // A GANTT lays a source's records out as bars on a DATA-DERIVED date window (one row per
                // record, startField..endField). ENTITY origin only; optional milestone diamonds overlay
                // a second source placed by its own date field.
                case "gantt":
                {
                    var gsrc = b["source"] as JsonObject;
                    var gse = ValidateBlockSource(gsrc, $"{where}: gantt", binding, boundEntity, ctx, errors);
                    if (gsrc != null && gse == null)
                    { errors.Add($"SEMANTIC: {where}: gantt needs an ENTITY source — only records carry dates to place on a timeline"); break; }
                    if (gse == null) break;
                    if (Str(b, "startField") is not { } gsf || ctx.FieldType(gse, gsf) is not ("date" or "datetime"))
                        errors.Add($"SEMANTIC: {where}: gantt startField '{Str(b, "startField")}' must be a date field on '{gse}'");
                    if (Str(b, "endField") is { } gef && ctx.FieldType(gse, gef) is not ("date" or "datetime"))
                        errors.Add($"SEMANTIC: {where}: gantt endField '{gef}' must be a date field on '{gse}'");
                    if (Str(b, "labelField") is { } glf && !ctx.FieldExists(gse, glf))
                        errors.Add($"SEMANTIC: {where}: gantt labelField '{glf}' is not a field of '{gse}'");
                    if (Str(b, "colorField") is { } gcf && ctx.FieldType(gse, gcf) != "select")
                        errors.Add($"SEMANTIC: {where}: gantt colorField '{gcf}' must be a select field on '{gse}'");
                    if (b["milestones"] is JsonObject mil)
                    {
                        var mse = ValidateBlockSource(mil["source"] as JsonObject, $"{where}: gantt milestones", binding, boundEntity, ctx, errors);
                        if (mse == null)
                            errors.Add($"SEMANTIC: {where}: gantt milestones needs an ENTITY source");
                        else
                        {
                            if (Str(mil, "dateField") is not { } mdf || ctx.FieldType(mse, mdf) is not ("date" or "datetime"))
                                errors.Add($"SEMANTIC: {where}: gantt milestones dateField must be a date field on '{mse}'");
                            if (Str(mil, "labelField") is { } mlf && !ctx.FieldExists(mse, mlf))
                                errors.Add($"SEMANTIC: {where}: gantt milestones labelField '{mlf}' is not a field of '{mse}'");
                        }
                    }
                    break;
                }
                // A FORM renders an entity's create/edit form inline, reusing its authored
                // `entity.form` layout — so a "start a request" surface can live on the page the
                // user is already looking at instead of only behind a New button.
                case "form":
                {
                    var fe3 = Str(b, "entity");
                    if (fe3 == null || !ctx.Entities.Contains(fe3))
                        errors.Add($"SEMANTIC: {where}: form entity '{fe3}' is unknown");
                    break;
                }
                // A SPLIT is a persistent master-detail two-pane: the list pane iterates `source`,
                // and `blocks` render for the SELECTED row (the same binding a repeat item gets).
                case "split":
                {
                    var spsrc = b["source"] as JsonObject;
                    var spe = ValidateBlockSource(spsrc, $"{where}: split", binding, boundEntity, ctx, errors);
                    if (spsrc != null && spe == null)
                        errors.Add($"SEMANTIC: {where}: split needs an ENTITY source — its list pane selects a record");
                    if (spe != null)
                        foreach (var f in Arr(b["fields"]))
                            if (f?.GetValue<string>() is { } fk && !ctx.FieldExists(spe, fk))
                                errors.Add($"SEMANTIC: {where}: split column '{fk}' is not a field of '{spe}'");
                    ValidateBlocks(b["blocks"], $"{where} split", "item", spe, ctx, errors);
                    break;
                }
                case "stat":
                {
                    var sf = Str(b, "field");
                    var ssrc = b["source"] as JsonObject;
                    if (b["link"] is JsonObject statLink) ValidateDeepLink(statLink, $"{where}: stat", ctx, errors);
                    ValidateCombinedSources(b, $"{where}: stat", binding, boundEntity, ctx, errors);
                    if (sf == null && ssrc == null && b["sources"] == null)
                    { errors.Add($"SEMANTIC: {where}: stat needs 'field' (record/item binding), a 'source' aggregate, or combined 'sources'"); break; }
                    if (sf != null)
                    {
                        if (binding is not ("record" or "item"))
                            errors.Add($"SEMANTIC: {where}: stat 'field' requires a record or repeat-item context");
                        else if (boundEntity != null && !ctx.FieldExists(boundEntity, sf))
                            errors.Add($"SEMANTIC: {where}: stat field '{sf}' is not a field of '{boundEntity}'");
                    }
                    if (ssrc != null) ValidateTileSource(ssrc, $"{where}: stat", binding, boundEntity, ctx, errors);
                    // `max` turns the number into a share of a denominator, which may be a whole-
                    // collection aggregate — validated exactly as a tile's, since they now render
                    // through the same meter.
                    switch (b["max"])
                    {
                        case JsonValue smv when smv.TryGetValue<string>(out string? smk):
                            if (binding is not ("record" or "item"))
                                errors.Add($"SEMANTIC: {where}: stat max '{smk}' as a field key needs a record or repeat-item context");
                            else if (!ctx.FieldExists(boundEntity, smk))
                                errors.Add($"SEMANTIC: {where}: stat max '{smk}' is not a field of '{boundEntity}'");
                            break;
                        case JsonObject smo:
                            if (smo["source"] is JsonObject sms)
                                ValidateTileSource(sms, $"{where}: stat max", binding, boundEntity, ctx, errors);
                            else errors.Add($"SEMANTIC: {where}: a stat max object needs a 'source'");
                            break;
                    }
                    if (Str(b, "format") == "share" && b["max"] == null)
                        errors.Add($"SEMANTIC: {where}: stat format 'share' needs a 'max' to be a share OF");
                    break;
                }
                case "field" or "chip" or "avatar" or "progress":
                {
                    var kind = Str(b, "kind")!;
                    var lf = Str(b, "field");
                    if (lf == null)
                    {
                        if (kind is "field" or "avatar" or "progress")
                            errors.Add($"SEMANTIC: {where}: '{kind}' needs a 'field'");
                        break; // a chip may use a literal 'value'
                    }
                    if (binding is not ("record" or "item"))
                        errors.Add($"SEMANTIC: {where}: '{kind}' requires a record or repeat-item context");
                    else
                        // A leaf may hop a relation too ('shift.start_time'): a cell that selects the right
                        // records but can only print their raw ids shows nothing useful.
                        ValidateFieldPath(lf, boundEntity, $"{where}: '{kind}'", ctx, errors);
                    if (kind == "progress" && b["max"] is JsonValue pv && pv.TryGetValue<string>(out var pmk)
                        && boundEntity != null && !ctx.FieldExists(boundEntity, pmk))
                        errors.Add($"SEMANTIC: {where}: progress max '{pmk}' is not a field of '{boundEntity}'");
                    break;
                }
                case "text":
                    break; // literal/interpolated text — interpolation refs are best-effort
                case "chart":
                {
                    if (b["link"] is JsonObject chartLink) ValidateDeepLink(chartLink, $"{where}: chart", ctx, errors);
                    // One source, or two-to-three drawn as separate series on ONE shared axis. The
                    // series must group by the same thing: mismatched axes render as a comparison
                    // that silently isn't one, which is worse than refusing to draw it.
                    var series = b["sources"] as JsonArray;
                    if (series != null && b["source"] != null)
                    { errors.Add($"SEMANTIC: {where}: chart gives both 'source' and 'sources' — use one"); break; }
                    if (series != null && Str(b, "chartType") is "pie" or "donut")
                        errors.Add($"SEMANTIC: {where}: a {Str(b, "chartType")} chart draws one series — use bar/line/area for 'sources'");

                    var srcs = series != null
                        ? series.OfType<JsonObject>().Select(s => s["source"] as JsonObject).ToList()
                        : [b["source"] as JsonObject];
                    string? firstGroupBy = null;
                    for (var i = 0; i < srcs.Count; i++)
                    {
                        var csrc = srcs[i];
                        var label = series != null ? $"chart series {i + 1}" : "chart";
                        var ce = Str(csrc, "entity");
                        if (ce == null || !ctx.Entities.Contains(ce))
                        { errors.Add($"SEMANTIC: {where}: {label} source entity '{ce}' is unknown"); continue; }
                        var gb = Str(csrc?["aggregate"] as JsonObject, "groupBy");
                        if (i == 0) firstGroupBy = gb;
                        else if (gb != firstGroupBy)
                            errors.Add($"SEMANTIC: {where}: {label} groups by '{gb}' but the first series groups by '{firstGroupBy}' — every series must share one axis");
                        if (gb != null && gb.StartsWith("month_of:", StringComparison.Ordinal))
                        {
                            // Calendar-month buckets — same dialect the aggregate endpoint and Home
                            // widgets speak ('revenue by month'), legal in block charts too.
                            var g = gb["month_of:".Length..];
                            if (ctx.FieldType(ce, g) is not ("date" or "datetime"))
                                errors.Add($"SEMANTIC: {where}: {label} groupBy '{gb}' needs a date/datetime field on '{ce}'");
                        }
                        else if (gb == null || !ctx.FieldExists(ce, gb))
                            errors.Add($"SEMANTIC: {where}: {label} groupBy '{gb}' is not a field of '{ce}'");
                        else if (ctx.FieldType(ce, gb) is { } gt && gt is not ("select" or "reference" or "date" or "datetime"))
                            errors.Add($"SEMANTIC: {where}: {label} groupBy '{gb}' must be a select/reference/date field (is '{gt}')");
                    }
                    break;
                }
                case "action":
                {
                    var cmd = Str(b, "command");

                    // SELF-ANCHORING: the button names the record itself, the way `cell` does, so it
                    // needs no record from the surface it sits on. A punch clock is the case that
                    // forced it — the first click of the day has nothing to bind to, and requiring
                    // somebody to open a form first is the thing the button exists to replace.
                    if (Str(b, "entity") is { } ae)
                    {
                        if (!ctx.Entities.Contains(ae))
                        { errors.Add($"SEMANTIC: {where}: action entity '{ae}' is unknown"); break; }
                        if (!ctx.IsCollection(ae))
                        { errors.Add($"SEMANTIC: {where}: action targets '{ae}', which is a {ctx.EntityKind.GetValueOrDefault(ae)} entity — there is only ever one of it, so 'keys' cannot identify which"); break; }

                        if (b["keys"] is not JsonObject akeys || akeys.Count == 0)
                        { errors.Add($"SEMANTIC: {where}: action on '{ae}' needs 'keys' identifying the record (e.g. {{\"datum\":\"{{{{today}}}}\"}})"); break; }
                        foreach (var (kk, _) in akeys)
                            if (!ctx.FieldExists(ae, kk))
                                errors.Add($"SEMANTIC: {where}: action key '{kk}' is not a field of '{ae}'");

                        if (cmd == null || !(ctx.CommandsByEntity.TryGetValue(ae, out var kcmds) && kcmds.ContainsKey(cmd)))
                            errors.Add($"SEMANTIC: {where}: action command '{cmd}' is not a command on '{ae}'");
                        break;
                    }

                    if (b["keys"] is JsonObject)
                    { errors.Add($"SEMANTIC: {where}: action has 'keys' but no 'entity' — keys identify a record of a named entity, so the two go together"); break; }

                    if (binding is not ("record" or "item"))
                        errors.Add($"SEMANTIC: {where}: 'action' requires a record or repeat-item context — or give it 'entity' + 'keys' to name the record itself");
                    else if (cmd == null || !(ctx.CommandsByEntity.TryGetValue(boundEntity ?? "", out var acmds) && acmds.ContainsKey(cmd)))
                        errors.Add($"SEMANTIC: {where}: action command '{cmd}' is not a command on '{boundEntity}'");
                    break;
                }
                case "create":
                {
                    // A page-level "New <record>" button, which OPENS THE FORM. Distinct from a
                    // self-anchoring `action`: that one identifies its record by keys and runs a
                    // command on it without asking anybody anything, which is the right shape for a
                    // toggle and the wrong one for "let me fill in a new expense".
                    // Without this kind, a page whose primary surface is a composed grid or calendar
                    // has no way at all to say "New", which is how the 2026-08-02 MeetingPrep app
                    // shipped with a display.text styled to look like a button and no create path.
                    var ce = Str(b, "entity");
                    if (binding is not "collection")
                        errors.Add($"SEMANTIC: {where}: 'create' belongs on a page — a record detail is already about one record, so use a child block or a command there");
                    else if (ce == null || !ctx.Entities.Contains(ce))
                        errors.Add($"SEMANTIC: {where}: create targets unknown entity '{ce}'");
                    else if (!ctx.IsCollection(ce))
                        errors.Add($"SEMANTIC: {where}: create targets '{ce}', which is a {ctx.EntityKind.GetValueOrDefault(ce)} entity — there is only ever one of it, so there is nothing to create");
                    break;
                }
            }
        }
    }

    /// <summary>Two repeats over the SAME date axis inside one layout container are the header strip
    /// and the body of one composed grid, so they must lay out the same way. A header running in a
    /// row above a body running in a column is not a grid — the strip sits above a stack of full-width
    /// rows and the two never line up.
    /// <para>Live 2026-08-02 (MeetingPrep): the header repeat carried <c>direction:"row"</c> and the
    /// body repeat omitted <c>direction</c> entirely, which defaults to <c>"column"</c>. That is the
    /// wrapping day-chips above a vertical list of empty day rows.</para>
    /// <para>SCOPE. Reported at the innermost container holding both, and NEVER across the page root:
    /// a horizontal week schedule and an unrelated vertical date picker on the same page may share a
    /// range without being one surface, and flagging that would be wrong. Verified over the 13
    /// reference apps: the two that build this grid (room-booking `availability`, timesheets
    /// `my_week`) place both repeats under one container and agree on <c>row</c> — zero flagged.
    /// Grouping is by the dates spec's literal JSON, so a spec written with keys in another order
    /// simply does not pair; conservative on purpose.</para></summary>
    private static void ValidatePairedDateAxes(JsonNode? blocks, string where, List<string> errors)
    {
        // Returns the (datesSpec -> effective directions) found in this subtree, and reports a
        // conflict at the innermost container that owns both sides of it.
        Dictionary<string, HashSet<string>> Walk(JsonNode? bs, bool isRoot)
        {
            var found = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            void Merge(Dictionary<string, HashSet<string>> from)
            {
                foreach (var (k, v) in from)
                    (found.TryGetValue(k, out var set) ? set : found[k] = new(StringComparer.Ordinal))
                        .UnionWith(v);
            }

            foreach (var bn in Arr(bs))
            {
                if (bn is not JsonObject b) continue;
                if (Str(b, "kind") == "repeat" && (b["source"] as JsonObject)?["dates"] is { } dates)
                {
                    var key = dates.ToJsonString();
                    (found.TryGetValue(key, out var set) ? set : found[key] = new(StringComparer.Ordinal))
                        .Add(Str(b, "direction") ?? "column");   // the schema default
                }
                var child = Walk(b["blocks"], isRoot: false);
                foreach (var tn in Arr(b["tabs"])) if (tn is JsonObject t) Merge(Walk(t["blocks"], false));
                foreach (var cn in Arr(b["columns"])) Merge(Walk(cn, false));

                // Report at THIS container when it is the first to hold both directions.
                foreach (var (key, dirs) in child)
                {
                    if (dirs.Count > 1)
                        errors.Add($"SEMANTIC: {where}: two repeats over the same date axis ({key}) "
                                 + $"lay out differently ({string.Join(" vs ", dirs.OrderBy(d => d, StringComparer.Ordinal))}) "
                                 + "— a header strip and its body columns must share `direction` (and give "
                                 + "each column box the same `width`), or the grid stops lining up");
                    // Collapse to one direction so an ancestor does not report the same conflict again.
                    (found.TryGetValue(key, out var s2) ? s2 : found[key] = new(StringComparer.Ordinal))
                        .UnionWith(dirs.Count > 1 ? [dirs.First()] : dirs);
                }
            }
            // At the page root the surviving set is deliberately NOT checked — see SCOPE above.
            return isRoot ? new() : found;
        }
        Walk(blocks, isRoot: true);
    }

    /// <summary>The origins a block's rows can come from. They are ALTERNATIVES, not layers — a repeat
    /// iterates exactly one. 'entity' yields records; the other three yield buckets that belong to no
    /// table, which is precisely what a second axis needs (a board's columns are days, and days have no
    /// table). An entity-only repeat can compose lists but never a grid.</summary>
    private static readonly string[] SourceOrigins = ["entity", "dates", "options", "platform"];

    /// <summary>Validates a repeat/stat/chart <c>source</c>. Returns the entity its rows are records of,
    /// or null for a bucket axis (whose item shape no entity names).</summary>
    private static string? ValidateBlockSource(JsonObject? src, string where, string binding,
        string? boundEntity, BlockCtx ctx, List<string> errors)
    {
        if (src == null) { errors.Add($"SEMANTIC: {where}: needs a 'source'"); return null; }

        var origins = SourceOrigins.Where(o => src[o] != null).ToArray();
        if (origins.Length == 0)
        {
            errors.Add($"SEMANTIC: {where}: source needs one origin — 'entity' (records), 'dates' (a date axis), "
                     + "'options' (a select field's choices) or 'platform' (the tenant directory)");
            return null;
        }
        if (origins.Length > 1)
        {
            errors.Add($"SEMANTIC: {where}: source gives {origins.Length} origins ({string.Join(", ", origins)}) — give exactly one");
            return null;
        }
        var origin = origins[0];

        // A bucket axis has no table behind it: `via`/`sort` have nothing to address, and the renderer
        // applies filters only to entity and platform rows. Accepting them would advertise a dead key.
        if (origin != "entity")
        {
            foreach (var dead in new[] { "via", "sort" })
                if (src[dead] != null)
                    errors.Add($"SEMANTIC: {where}: source '{dead}' has no meaning on a '{origin}' axis — it applies to an 'entity' source only");
            if (origin != "platform" && src["filters"] != null)
                errors.Add($"SEMANTIC: {where}: source 'filters' are not applied to a '{origin}' axis — bound it with the axis's own keys ('from'/'to'/'count') instead");
        }

        if (origin == "options")
        {
            var opt = src["options"] as JsonObject;
            var oe = Str(opt, "entity");
            var of = Str(opt, "field");
            if (oe == null || !ctx.Entities.Contains(oe))
                errors.Add($"SEMANTIC: {where}: source options entity '{oe}' is unknown");
            else if (of == null || !ctx.FieldExists(oe, of))
                errors.Add($"SEMANTIC: {where}: source options field '{of}' is not a field of '{oe}'");
            else if (ctx.FieldType(oe, of) is { } ot && ot is not ("select" or "multiselect"))
                errors.Add($"SEMANTIC: {where}: source options field '{oe}.{of}' must be a select field to have options (is '{ot}')");
            return null;
        }
        if (origin is "dates" or "platform")
        {
            foreach (var fn in Arr(src["filters"]))
                if (fn is JsonObject pf) ValidateFilterAddress(pf, null, where, ctx, errors);
            ValidateFilterIntervals(src["filters"], where, errors);
            return null;
        }

        var re = Str(src, "entity");
        if (re == null || !ctx.Entities.Contains(re))
        { errors.Add($"SEMANTIC: {where}: source entity '{re}' is unknown"); return null; }

        // `via` scopes the rows to the CURRENT record or repeat-item — this is what makes nested
        // groupings possible (events → the stations of THAT event → the people on THAT station).
        if (Str(src, "via") is { } rvia)
        {
            if (binding is not ("record" or "item"))
                errors.Add($"SEMANTIC: {where}: source 'via' needs a record or repeat-item context");
            else if (!ctx.FieldDefs.GetValueOrDefault(re, new()).TryGetValue(rvia, out var rvf)
                     || Str(rvf, "type") != "reference" || Str(rvf, "targetEntity") != boundEntity)
                errors.Add($"SEMANTIC: {where}: source via '{re}.{rvia}' must be a reference to '{boundEntity}'");
        }
        foreach (var fn in Arr(src["filters"]))
            if (fn is JsonObject fo) ValidateFilterAddress(fo, re, where, ctx, errors);
        ValidateFilterIntervals(src["filters"], where, errors);
        foreach (var sn in Arr(src["sort"]))
            if (sn is JsonObject so && Str(so, "field") is { } sf && !ctx.FieldExists(re, sf))
                errors.Add($"SEMANTIC: {where}: source sort field '{sf}' is not a field of '{re}'");
        return re;
    }

    /// <summary>Two range filters on the SAME address that can never both hold. Every filter in a
    /// source must pass, so such a pair matches nothing — and a source that matches nothing does not
    /// fail, it renders empty. With <c>emptyText:""</c> it renders as nothing at all.
    /// <para>Live 2026-08-02 (MeetingPrep): every cell of the week grid filtered
    /// <c>start_at gte {{day.date}}</c> AND <c>start_at lt {{day.date}}</c> — the same value on both
    /// bounds — so no meeting could ever appear in any cell, on any day, forever. It looked like an
    /// empty calendar. The model wrote it because a day bucket was genuinely inexpressible until
    /// <c>{{day.next}}</c> existed; the message names that fix.</para>
    /// <para>Only an EMPTY interval is an error. <c>gte x</c> + <c>lte x</c> is the closed interval
    /// [x,x], which legitimately matches exactly x.</para></summary>
    private static void ValidateFilterIntervals(JsonNode? filters, string where, List<string> errors)
    {
        foreach (var group in Arr(filters).OfType<JsonObject>()
                     .Where(f => Str(f, "field") is not null || Str(f, "path") is not null)
                     .GroupBy(f => Str(f, "field") ?? Str(f, "path")!))
        {
            var byOp = group.Where(f => Str(f, "operator") is { } o
                                        && o is "gt" or "gte" or "lt" or "lte")
                            .ToList();
            foreach (var lower in byOp.Where(f => Str(f, "operator") is "gt" or "gte"))
            foreach (var upper in byOp.Where(f => Str(f, "operator") is "lt" or "lte"))
            {
                // Same literal value on both bounds; a template ({{day.date}}) compares as its text,
                // which is exactly right — two references to the same expression resolve alike.
                if (lower["value"]?.ToJsonString() != upper["value"]?.ToJsonString()) continue;
                var lo = Str(lower, "operator")!;
                var hi = Str(upper, "operator")!;
                if (lo == "gte" && hi == "lte") continue;              // [x,x] — matches exactly x
                var v = lower["value"]?.ToJsonString() ?? "null";
                errors.Add($"SEMANTIC: {where}: filters '{group.Key} {lo} {v}' and '{group.Key} {hi} {v}' "
                         + "can never both be true — this source matches nothing, and with emptyText:\"\" "
                         + "that renders as a silently empty surface. To bucket a datetime into one day, "
                         + "compare against the NEXT bucket: gte {{day.date}} and lt {{day.next}}");
            }
        }
    }

    /// <summary>A filter addresses its value by 'field' (a key on the row) or 'path' (one hop through a
    /// reference) — exactly one. A misaddressed filter does not fail loudly, it silently matches nothing,
    /// so a board would render empty cells rather than an error.</summary>
    private static void ValidateFilterAddress(JsonObject f, string? entity, string where, BlockCtx ctx, List<string> errors)
    {
        var field = Str(f, "field");
        var path = Str(f, "path");
        if (field != null && path != null)
        { errors.Add($"SEMANTIC: {where}: filter gives both 'field' and 'path' — use one"); return; }
        if (field == null && path == null)
        { errors.Add($"SEMANTIC: {where}: filter needs a 'field' or a 'path'"); return; }
        ValidateFieldPath(field ?? path!, entity, $"{where}: filter", ctx, errors);
        ValidateOperatorShape(f, k => ctx.FieldExists(entity, k), entity, $"{where}: filter", errors);
    }

    /// <summary>Resolves a plain field key or ONE hop ('shift.shift_date') against <paramref name="entity"/>.
    /// The hop must go through a reference and land on a plain value: a hop landing on another reference
    /// renders a raw id, and a second hop is not supported.</summary>
    private static void ValidateFieldPath(string path, string? entity, string where, BlockCtx ctx, List<string> errors)
    {
        // A bucket axis (dates/options/platform) binds items whose shape no entity names — validated
        // best-effort, like 'text' interpolation.
        if (entity == null) return;

        var dot = path.IndexOf('.');
        if (dot < 0)
        {
            if (!ctx.FieldExists(entity, path))
                errors.Add($"SEMANTIC: {where}: '{path}' is not a field of '{entity}'");
            return;
        }
        var refKey = path[..dot];
        var rest = path[(dot + 1)..];
        if (!ctx.FieldDefs.GetValueOrDefault(entity, new()).TryGetValue(refKey, out var rf))
        { errors.Add($"SEMANTIC: {where}: path '{path}' hops '{refKey}', which is not a field of '{entity}'"); return; }
        if (Str(rf, "type") != "reference")
        {
            errors.Add($"SEMANTIC: {where}: path '{path}' hops '{entity}.{refKey}', which is a '{Str(rf, "type")}' — "
                     + "only a reference field can be hopped through");
            return;
        }
        // Platform people are resolved by the renderer into a person chip; their directory fields are not
        // in this manifest, so a hop into one reads nothing.
        if (Str(rf, "targetApp") == "platform")
        {
            errors.Add($"SEMANTIC: {where}: path '{path}' hops into the platform directory — read '{refKey}' "
                     + "directly (the renderer resolves a person reference to a name chip) instead of hopping through it");
            return;
        }
        var target = Str(rf, "targetEntity");
        if (target == null || !ctx.Entities.Contains(target))
        { errors.Add($"SEMANTIC: {where}: path '{path}' hops '{entity}.{refKey}', whose targetEntity '{target}' is unknown"); return; }
        if (!ctx.FieldExists(target, rest))
        { errors.Add($"SEMANTIC: {where}: path '{path}' reads '{rest}', which is not a field of '{target}'"); return; }
        if (ctx.FieldType(target, rest) == "reference")
            errors.Add($"SEMANTIC: {where}: path '{path}' lands on reference '{target}.{rest}', which renders as a raw id — "
                     + "hop to a plain value on it instead (a second hop is not supported)");
    }

    private static readonly string[] AttentionOps = ["eq", "neq", "gt", "gte", "lt", "lte"];

    private static void ValidateTiles(JsonNode? tiles, string where, string binding, string? boundEntity,
        BlockCtx ctx, List<string> errors)
    {
        foreach (var tn in Arr(tiles))
        {
            if (tn is not JsonObject t) continue;
            var label = Str(t, "label") ?? "?";
            var tw = $"{where}: tile '{label}'";
            if (t["link"] is JsonObject tileLink) ValidateDeepLink(tileLink, tw, ctx, errors);
            var field = Str(t, "field");
            var src = t["source"] as JsonObject;
            if (field != null)
            {
                // record OR repeat-item — a tile row inside a repeat reads that item, which is how a
                // per-row share of a collection total gets rendered.
                if (binding is not ("record" or "item"))
                    errors.Add($"SEMANTIC: {tw}: 'field' tiles need a record or repeat-item context — use a 'source' on pages");
                else if (!ctx.FieldExists(boundEntity, field))
                    errors.Add($"SEMANTIC: {tw}: field '{field}' is not a field of '{boundEntity}'");
            }
            if (field == null && src == null && t["sources"] == null)
                errors.Add($"SEMANTIC: {tw}: needs either 'field' (record binding), a 'source', or combined 'sources'");
            if (src != null) ValidateTileSource(src, tw, binding, boundEntity, ctx, errors);
            ValidateCombinedSources(t, tw, binding, boundEntity, ctx, errors);

            switch (t["max"])
            {
                case JsonValue mv when mv.TryGetValue<string>(out string? mk):
                    if (binding is not ("record" or "item"))
                        errors.Add($"SEMANTIC: {tw}: max '{mk}' as a field key needs a record or repeat-item context");
                    else if (!ctx.FieldExists(boundEntity, mk))
                        errors.Add($"SEMANTIC: {tw}: max '{mk}' is not a field of '{boundEntity}'");
                    break;
                case JsonObject mo:
                    if (mo["source"] is JsonObject ms) ValidateTileSource(ms, $"{tw} max", binding, boundEntity, ctx, errors);
                    else errors.Add($"SEMANTIC: {tw}: a max object needs a 'source'");
                    break;
            }
            if (Str(t, "format") == "share" && t["max"] == null)
                errors.Add($"SEMANTIC: {tw}: format 'share' needs a 'max' to be a share OF");
            if (t["attention"] is JsonObject att && Str(att, "op") is { } aop && !AttentionOps.Contains(aop))
                errors.Add($"SEMANTIC: {tw}: attention op '{aop}' is invalid (allowed: {string.Join(", ", AttentionOps)})");
        }
    }

    /// <summary>`sources` + `combine` on a stat/tile: two or three aggregates folded into ONE number.
    /// This exists because a single aggregate is one op on one field, so a collection-level RATIO —
    /// a portfolio multiple is sum(value)/sum(cost) — cannot be said at all (the average of the
    /// per-row ratios is a different, wrong number). They are alternatives to `source`, not layers:
    /// authoring both would leave the renderer guessing which one the number is.</summary>
    private static void ValidateCombinedSources(JsonObject b, string where, string binding,
        string? boundEntity, BlockCtx ctx, List<string> errors)
    {
        var sources = b["sources"] as JsonArray;
        var combine = b["combine"] as JsonObject;
        if (sources == null)
        {
            if (combine != null) errors.Add($"SEMANTIC: {where}: 'combine' needs 'sources' to fold");
            return;
        }
        if (b["source"] != null)
            errors.Add($"SEMANTIC: {where}: gives both 'source' and 'sources' — use one");
        if (b["field"] != null)
            errors.Add($"SEMANTIC: {where}: gives both 'field' and 'sources' — use one");
        var mode = Str(combine, "mode");
        if (mode == null)
            errors.Add($"SEMANTIC: {where}: 'sources' needs a 'combine' mode saying how they fold into one number");
        else if (mode == "ratio" && sources.Count != 2)
            errors.Add($"SEMANTIC: {where}: combine 'ratio' takes exactly 2 sources (has {sources.Count})");
        for (var i = 0; i < sources.Count; i++)
            if (sources[i] is JsonObject s) ValidateTileSource(s, $"{where}: source {i + 1}", binding, boundEntity, ctx, errors);
    }

    private static void ValidateTileSource(JsonObject src, string where, string binding, string? boundEntity,
        BlockCtx ctx, List<string> errors)
    {
        var se = Str(src, "entity");
        if (se == null || !ctx.Entities.Contains(se))
        { errors.Add($"SEMANTIC: {where}: source entity '{se}' is unknown"); return; }
        if (Str(src, "via") is { } via)
        {
            // record OR repeat-item: `via` scopes an aggregate to the current row, so a nested board can
            // count "the people on THIS station" the same way a detail counts "this record's children".
            if (binding is not ("record" or "item"))
                errors.Add($"SEMANTIC: {where}: source 'via' needs a record or repeat-item context");
            else if (!ctx.FieldDefs.GetValueOrDefault(se, new()).TryGetValue(via, out var vf)
                     || Str(vf, "type") != "reference" || Str(vf, "targetEntity") != boundEntity)
                errors.Add($"SEMANTIC: {where}: source via '{se}.{via}' must be a reference to '{boundEntity}'");
        }
        var agg = src["aggregate"] as JsonObject;
        var op = Str(agg, "op");
        var af = Str(agg, "field");
        if (af != null)
        {
            if (!ctx.FieldExists(se, af))
                errors.Add($"SEMANTIC: {where}: aggregate field '{af}' is not a field of '{se}'");
            else if (op is "sum" or "avg" && ctx.FieldType(se, af) is { } ft && ft is not ("integer" or "decimal" or "money"))
                errors.Add($"SEMANTIC: {where}: aggregate op '{op}' needs a numeric field but '{se}.{af}' is '{ft}'");
        }
        else if (op is "sum" or "avg" or "min" or "max")
            errors.Add($"SEMANTIC: {where}: aggregate op '{op}' requires a 'field'");
        foreach (var fn in Arr(src["filters"]))
            if (fn is JsonObject f && Str(f, "field") is { } ff && !ctx.FieldExists(se, ff))
                errors.Add($"SEMANTIC: {where}: filter references unknown field '{ff}' on '{se}'");
    }

    /// <summary>Authored form layouts (draft binding): only field references need semantic checks —
    /// the formBlock schema already restricts kinds to section/fields. Base fields never belong on
    /// a form (the runtime provides them).</summary>
    private static void ValidateFormBlocks(JsonNode? blocks, string where, string entity, BlockCtx ctx, List<string> errors)
    {
        foreach (var bn in Arr(blocks))
        {
            if (bn is not JsonObject b) continue;
            switch (Str(b, "kind"))
            {
                case "fields":
                    foreach (var fk in Arr(b["fields"]))
                        if (fk?.GetValue<string>() is { } key)
                        {
                            if (BaseFields.Contains(key))
                                errors.Add($"SEMANTIC: {where}: base field '{key}' is runtime-provided and cannot be on the form");
                            else if (!ctx.FieldExists(entity, key))
                                errors.Add($"SEMANTIC: {where}: fields block references unknown field '{key}' on entity '{entity}'");
                        }
                    break;
                case "section":
                    ValidateFormBlocks(b["blocks"], where, entity, ctx, errors);
                    break;
            }
        }
    }

    // ---- design layer: component configs vs the ComponentCatalog contract -----------------------

    /// <summary>Base fields are runtime-provided and not in the definition's field defs — their
    /// types matter to the design layer (e.g. grouping a chart by month_of:created_at).</summary>
    private static readonly Dictionary<string, string> BaseFieldTypes = new()
    {
        ["id"] = "text", ["company_id"] = "text", ["app_id"] = "text",
        ["created_at"] = "datetime", ["updated_at"] = "datetime", ["deleted_at"] = "datetime",
        ["created_by"] = "text", ["updated_by"] = "text", ["record_state"] = "select",
    };

    private static readonly string[] FilterOperators =
        ["eq", "neq", "gt", "gte", "lt", "lte", "contains", "in", "notIn", "isEmpty", "isNotEmpty", "between", "overlaps"];

    private static readonly Dictionary<string, JsonSchema> CompiledConfigSchemas = new();

    /// <summary>Design-layer validation: every view config and widget body is checked against the
    /// ComponentCatalog's configSchema plus field-level resolution rules the JSON Schema cannot
    /// express (columns/sort/groupBy/date fields must exist and have the right type). Empty when
    /// the document authors no UI. Messages are prefixed DESIGN so the repair prompt can group them.</summary>
    public static List<string> DesignErrors(JsonNode? doc)
    {
        var errors = new List<string>();
        if (doc is not JsonObject root) return errors;

        var entities = new HashSet<string>();
        var fieldDefs = new Dictionary<string, Dictionary<string, JsonObject>>();
        foreach (var en in Arr(root["entities"]))
        {
            if (en is not JsonObject ent || Str(ent, "key") is not { } ekey) continue;
            entities.Add(ekey);
            var defs = new Dictionary<string, JsonObject>();
            foreach (var fn in Arr(ent["fields"]))
                if (fn is JsonObject f && Str(f, "key") is { } fk)
                    defs[fk] = f;
            fieldDefs[ekey] = defs;
        }
        bool FieldExists(string? e, string? f) =>
            f != null && (BaseFields.Contains(f) || (e != null && fieldDefs.TryGetValue(e, out var d) && d.ContainsKey(f)));
        string? FieldType(string? e, string? f) =>
            f == null ? null
            : BaseFieldTypes.TryGetValue(f, out var bt) ? bt
            : e != null && fieldDefs.TryGetValue(e, out var d) && d.TryGetValue(f, out var fd) ? Str(fd, "type") : null;
        var ctx = new DesignCtx(entities, FieldExists, FieldType);

        foreach (var vn in Arr(root["views"]))
        {
            if (vn is not JsonObject v) continue;
            var vkey = Str(v, "key") ?? "";
            var vtype = Str(v, "type");
            var vent = Str(v, "entity");
            if (v["config"] is not JsonObject cfg || vtype is null) continue;
            var where = $"view '{vkey}'";

            if (ComponentCatalog.Find("view." + vtype) is { } comp)
                EvaluateConfig(comp, cfg, where, errors);

            void CheckFields(string prop, params string?[] keys)
            {
                foreach (var k in keys)
                    if (k != null && !FieldExists(vent, k))
                        errors.Add($"DESIGN: {where} {prop} '{k}' is not a field of '{vent}'");
            }
            switch (vtype)
            {
                case "table":
                    foreach (var c in Arr(cfg["columns"]))
                        if (c?.GetValue<string>() is { } col) CheckFields("column", col);
                    foreach (var s in Arr(cfg["defaultSort"]))
                        if (s is JsonObject so) CheckFields("defaultSort field", Str(so, "field"));
                    if (cfg["filterBar"] is JsonObject vfb)
                    {
                        foreach (var fn in Arr(vfb["search"]))
                            if (fn?.GetValue<string>() is { } fk) CheckFields("filterBar search field", fk);
                        foreach (var fn in Arr(vfb["facets"]))
                            if (fn?.GetValue<string>() is { } fk)
                            {
                                CheckFields("filterBar facet", fk);
                                if (FieldType(vent, fk) is { } ft && ft is not ("select" or "multiselect" or "reference"))
                                    errors.Add($"DESIGN: {where} filterBar facet '{fk}' must be a select/multiselect/reference field (is '{ft}')");
                            }
                    }
                    break;
                case "kanban":
                    foreach (var c in Arr(cfg["cardFields"]))
                        if (c?.GetValue<string>() is { } cf) CheckFields("cardField", cf);
                    CheckFields("swimlaneField", Str(cfg, "swimlaneField"));
                    break;
                case "calendar":
                    foreach (var dk in new[] { "dateField", "endDateField" })
                        if (Str(cfg, dk) is { } df)
                        {
                            if (!FieldExists(vent, df)) errors.Add($"DESIGN: {where} {dk} '{df}' is not a field of '{vent}'");
                            else if (FieldType(vent, df) is not ("date" or "datetime"))
                                errors.Add($"DESIGN: {where} {dk} '{df}' must be a date/datetime field");
                        }
                    CheckFields("titleField", Str(cfg, "titleField"));
                    CheckFields("colorByField", Str(cfg, "colorByField"));
                    // A day view rules an HOUR axis. Anchored on a plain 'date' there is no time of
                    // day to place anything at, so every record would pile onto hour zero and the
                    // surface would read as broken rather than as misconfigured.
                    if (Str(cfg, "range") == "day" && Str(cfg, "dateField") is { } dayField
                        && FieldExists(vent, dayField) && FieldType(vent, dayField) != "datetime")
                        errors.Add($"DESIGN: {where} range 'day' needs a 'datetime' dateField — '{dayField}' is a '{FieldType(vent, dayField)}', which carries no time of day");
                    break;
                case "timeline":
                    if (Str(cfg, "dateField") is { } tdf)
                    {
                        if (!FieldExists(vent, tdf)) errors.Add($"DESIGN: {where} dateField '{tdf}' is not a field of '{vent}'");
                        else if (FieldType(vent, tdf) is not ("date" or "datetime"))
                            errors.Add($"DESIGN: {where} dateField '{tdf}' must be a date/datetime field");
                    }
                    CheckFields("groupBy", Str(cfg, "groupBy"));
                    break;
                case "dashboard":
                    ValidateWidgets(cfg["widgets"], where, ctx, errors);
                    break;
            }
        }

        foreach (var pn in Arr(root["pages"]))
            if (pn is JsonObject page)
                ValidatePageWidgets(page["blocks"], $"page '{Str(page, "key")}'", ctx, errors);

        // Subordinate entities live inside their parent's detail — they never get their own
        // pages or standalone views (that's the duplicate-child-table bug the design pass must not
        // reintroduce). Settings singletons get a settings page block, not list views.
        var subordinate = new HashSet<string>();
        var settingsKind = new HashSet<string>();
        foreach (var en in Arr(root["entities"]))
        {
            if (en is not JsonObject ent || Str(ent, "key") is not { } ek) continue;
            if (ent["ownedBy"] is JsonObject) subordinate.Add(ek);
            if (Str(ent, "kind") == "settings") settingsKind.Add(ek);
        }
        foreach (var vn in Arr(root["views"]))
            if (vn is JsonObject v && Str(v, "entity") is { } ve)
            {
                if (subordinate.Contains(ve))
                    errors.Add($"DESIGN: view '{Str(v, "key")}' targets subordinate entity '{ve}' — children render as child blocks inside the parent's detail, never as standalone views");
                if (settingsKind.Contains(ve))
                    errors.Add($"DESIGN: view '{Str(v, "key")}' targets settings entity '{ve}' — settings singletons get a {{\"kind\":\"settings\"}} page block, not list views");
            }
        foreach (var pn in Arr(root["pages"]))
            if (pn is JsonObject page && Str(page, "entity") is { } pe && subordinate.Contains(pe))
                errors.Add($"DESIGN: page '{Str(page, "key")}' targets subordinate entity '{pe}' — children render inside the parent's detail, not as pages");

        return errors;
    }

    private sealed record DesignCtx(
        HashSet<string> Entities,
        Func<string?, string?, bool> FieldExists,
        Func<string?, string?, string?> FieldType);

    /// <summary>Recurse a page's blocks and validate every 'widgets' block's widget bodies.</summary>
    private static void ValidatePageWidgets(JsonNode? blocks, string where, DesignCtx ctx, List<string> errors)
    {
        foreach (var bn in Arr(blocks))
        {
            if (bn is not JsonObject b) continue;
            switch (Str(b, "kind"))
            {
                case "widgets": ValidateWidgets(b["widgets"], where, ctx, errors); break;
                case "tabs":
                    foreach (var tn in Arr(b["tabs"]))
                        if (tn is JsonObject t) ValidatePageWidgets(t["blocks"], where, ctx, errors);
                    break;
                case "section": ValidatePageWidgets(b["blocks"], where, ctx, errors); break;
                case "columns":
                    foreach (var cn in Arr(b["columns"]))
                        ValidatePageWidgets(cn, where, ctx, errors);
                    break;
            }
        }
    }

    private static void ValidateWidgets(JsonNode? widgets, string where, DesignCtx ctx, List<string> errors)
    {
        foreach (var wn in Arr(widgets))
        {
            if (wn is not JsonObject w) { errors.Add($"DESIGN: {where}: every widget must be an object"); continue; }
            var wtype = Str(w, "type");
            var comp = wtype is null ? null : ComponentCatalog.Find("widget." + wtype);
            if (comp is null)
            {
                errors.Add($"DESIGN: {where}: widget type '{wtype ?? "(missing)"}' is unknown (allowed: metric, chart, list — " +
                           "shape { \"type\", \"source\": { \"entity\", \"aggregate\": { \"op\", \"field\"?, \"groupBy\"? }, \"filters\"? }, \"viz\"? })");
                continue;
            }
            EvaluateConfig(comp, w, $"{where} widget", errors);

            var src = w["source"] as JsonObject;
            var se = Str(src, "entity");
            if (se == null || !ctx.Entities.Contains(se))
            { errors.Add($"DESIGN: {where}: widget source entity '{se}' is unknown"); continue; }

            var agg = src?["aggregate"] as JsonObject;
            var op = Str(agg, "op");
            var afield = Str(agg, "field");
            if (afield != null)
            {
                if (!ctx.FieldExists(se, afield))
                    errors.Add($"DESIGN: {where}: widget aggregate field '{afield}' is not a field of '{se}'");
                else if (op is "sum" or "avg" && ctx.FieldType(se, afield) is { } ft && ft is not ("integer" or "decimal" or "money"))
                    errors.Add($"DESIGN: {where}: aggregate op '{op}' needs a numeric field but '{se}.{afield}' is '{ft}'");
            }
            else if (op is "sum" or "avg" or "min" or "max")
                errors.Add($"DESIGN: {where}: aggregate op '{op}' requires an aggregate 'field'");

            var groupBy = Str(agg, "groupBy");
            if (groupBy != null)
            {
                if (groupBy.StartsWith("month_of:", StringComparison.Ordinal))
                {
                    var g = groupBy["month_of:".Length..];
                    if (ctx.FieldType(se, g) is not ("date" or "datetime"))
                        errors.Add($"DESIGN: {where}: groupBy '{groupBy}' needs a date/datetime field on '{se}'");
                }
                else if (!ctx.FieldExists(se, groupBy))
                    errors.Add($"DESIGN: {where}: widget groupBy '{groupBy}' is not a field of '{se}'");
            }
            if (wtype == "chart" && groupBy is null)
                errors.Add($"DESIGN: {where}: chart widgets need source.aggregate.groupBy (what to group by)");

            foreach (var fn in Arr(src?["filters"]))
                if (fn is JsonObject f)
                {
                    if (Str(f, "field") is { } ff && !ctx.FieldExists(se, ff))
                        errors.Add($"DESIGN: {where}: widget filter references unknown field '{ff}' on '{se}'");
                    if (Str(f, "operator") is { } fo && !FilterOperators.Contains(fo))
                        errors.Add($"DESIGN: {where}: widget filter operator '{fo}' is invalid (allowed: {string.Join(", ", FilterOperators)})");
                }
            foreach (var sn in Arr(src?["sort"]))
                if (sn is JsonObject s && Str(s, "field") is { } sf && !ctx.FieldExists(se, sf))
                    errors.Add($"DESIGN: {where}: widget sort references unknown field '{sf}' on '{se}'");
            foreach (var vf in Arr((w["viz"] as JsonObject)?["fields"]))
                if (vf?.GetValue<string>() is { } vk && !ctx.FieldExists(se, vk))
                    errors.Add($"DESIGN: {where}: widget viz field '{vk}' is not a field of '{se}'");
        }
    }

    /// <summary>Evaluate a config/widget body against a catalog component's configSchema. Same
    /// pruned hierarchical walk as StructuralErrors (no `if`-probe or passed-oneOf-branch
    /// phantoms); enum errors list allowed values.</summary>
    private static void EvaluateConfig(ComponentDef comp, JsonObject body, string where, List<string> errors)
    {
        JsonSchema schema;
        lock (CompiledConfigSchemas)
        {
            if (!CompiledConfigSchemas.TryGetValue(comp.Id, out schema!))
                CompiledConfigSchemas[comp.Id] = schema = JsonSchema.FromText(comp.ConfigSchema.ToJsonString());
        }
        var results = schema.Evaluate(body.Deserialize<JsonElement>(), new EvaluationOptions { OutputFormat = OutputFormat.Hierarchical });
        if (results.IsValid) return;
        CollectSchemaErrors(results, comp.ConfigSchema,
            (loc, msg) => errors.Add($"DESIGN: {where} config [{loc}]: {msg} (component {comp.Id})"));
    }

    /// <summary>
    /// The host of a webhook URL that cannot be a real service, or null when it might be.
    ///
    /// <para>This exists because a model that meets a capability gap will invent infrastructure to
    /// cover it. Asked to "roll pricing plans, hiring lines and cost lines up into the period rows",
    /// a generated budget app declared a command that POSTed to
    /// <c>https://automation.internal.invalid/budget_planner/recalculate</c> and reported
    /// "Recalculation queued — period figures and runway will refresh". Nothing was queued and nothing
    /// ever refreshed: <c>.invalid</c> is the reserved TLD for names guaranteed never to resolve. The
    /// user pressed Recalculate Plan, was told it worked, and watched every figure stay at zero (live
    /// 2026-08-05).</para>
    ///
    /// <para>A button that lies is worse than a missing button, and this is cheap to refuse. The
    /// reserved names are RFC 2606 / RFC 6761 plus the conventional private suffixes; a real
    /// integration reaches a host somebody configured, which none of these can be.</para>
    /// </summary>
    private static string? FabricatedHost(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var host = uri.Host.ToLowerInvariant();
        if (host is "localhost" || host.StartsWith("127.", StringComparison.Ordinal)) return host;
        // Suffixes, so a subdomain is caught too — `hooks.example.com` is exactly as fictional as
        // `example.com`, and it is the more likely thing for a model to write.
        string[] reserved =
        [
            ".invalid", ".example", ".test", ".localhost", ".local", ".internal", ".lan",
            "example.com", "example.org", "example.net",
        ];
        return reserved.Any(suffix => host.EndsWith(suffix, StringComparison.Ordinal)) ? host : null;
    }

    private static string? Str(JsonNode? n, string prop) =>
        n?[prop] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string? Str(JsonObject? n, string prop) =>
        n != null && n[prop] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static JsonArray Arr(JsonNode? n) => n as JsonArray ?? new JsonArray();
}

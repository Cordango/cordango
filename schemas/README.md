# App Definition — the contract

The **App Definition** is the single, versioned, validated document that fully
describes an application. Per [architecture-strategy.md](../_docs/10-platform/architecture-strategy.md) §1.3 it is the one contract shared
by the AI, the runtime, the UI renderer, and the dev→prod promotion pipeline. The AI
emits this document — **never freeform code** — and a document that does not validate is
never deployed.

## Files

| File | Purpose |
|------|---------|
| `app-definition.schema.json` | JSON Schema (Draft 2020-12) — the formal contract. **Generated** from `src/` by `compose.py`, but checked in (it is an embedded resource and the single artifact everything downstream consumes). |
| `src/*.json` | The per-concern SOURCE files — edit these, never the composed file. |
| `compose.py` | Composes `src/` into `app-definition.schema.json`; `--check` is the CI drift guard. |
| `examples/crm.appdef.json` | A complete, valid CRM app definition. |
| `examples/invalid/broken.appdef.json` | Deliberately broken — proves the schema rejects bad input. |
| `validate.py` | Reusable validator (used by CI and the AI repair loop). |

## Source layout (edit `src/`, then compose)

The schema is authored as per-concern source files and composed into the one
stored contract — the STORED App Definition stays a single document; only the
schema's source is split:

| Source file | Owns |
|------|---------|
| `src/root.json` | Envelope + metadata/archetype/plugins; shared primitives (`identifier`, `fieldPath`, `filter`, …). |
| `src/domain.json` | `entities`, `relations` — the data model. |
| `src/ui.json` | `presentation`, `views`, `pages`, `theme` + the whole block system. |
| `src/behavior.json` | `processes`, `commands`, `workflows` + `condition`/`effect` plane. |
| `src/notifications.json` | `effect_notify`, `effect_email`. |
| `src/security.json` | `roles`. |

Workflow:

```
# edit schema/src/<concern>.json
python schema/compose.py            # regenerate app-definition.schema.json
python schema/compose.py --check    # CI drift guard (fails if src/ and composed file differ)
```

Commit BOTH the source file and the recomposed file. The api container must be
restarted afterwards — the embedded schema cannot hot-reload. Rules the composer
enforces: a key defined in two source files is an error; every key must be listed
in `compose.py`'s `MANIFEST` (the concern-ownership record); def bodies are copied
verbatim (the block/effect unions keep their `allOf` if/then const dispatch —
`unevaluatedProperties` is forbidden, it crashes the JsonSchema.Net test host).

## Validate

```
python -m pip install jsonschema
python schema/validate.py schema/examples/crm.appdef.json
```

Exit code 0 = valid, 1 = invalid (with a list of errors + JSON paths). This is the exact
gate the **validate→repair loop** uses: the AI regenerates until validation passes, then
a human previews before go-live.

## What the document contains

- **entities / fields** — become real, typed Postgres tables/columns (schema-on-write,
  architecture-strategy.md §1.1). Base fields (`Id`, `CompanyId`, `AppId`, `CreatedAt/By`, `UpdatedAt/By`,
  `DeletedAt`, `Status`) are provided by the runtime and MUST NOT be declared here.
- **relations** — to-many / many-to-many. To-one relations are `reference` fields.
- **views** — table / detail / kanban / calendar / timeline / dashboard.
- **workflows** — WHEN (trigger) / IF (conditions) / THEN (actions).
- **roles** — per-entity CRUD grants with optional field-level overrides.
- **theme / navigation** — branding and layout.
- **plugins** — first-party or custom plugins the app enables.

## Field types (v1)

`text`, `longtext`, `integer`, `decimal`, `money`, `boolean`, `date`, `datetime`,
`email`, `url`, `phone`, `select`, `multiselect`, `reference`, `json`, `attachment`.

Type-specific rules enforced by the schema:
- `select` / `multiselect` require `options`.
- `reference` requires `targetEntity` (and may set `onDelete`).

## Limits of JSON Schema — the semantic validator is a separate, required layer

JSON Schema checks **structure**. It cannot check **referential integrity across the
document**. Before deploy, a semantic validator (next building block) must also enforce:

- every `reference.targetEntity` names an entity that exists;
- every `view.entity`, `navigation.item.view`, `relation.fromEntity/toEntity`,
  `filter.field`, `sort.field`, and `role.grant.entity` resolves to something real;
- `entity.displayField` and `relation.inverseField` exist on the right entity;
- keys are unique within their scope (entity keys, field keys per entity, view keys, …);
- no reference cycles that the storage layer can't satisfy.

Structural validation (this schema) + semantic validation (that layer) together are the
full "valid App Definition" gate referenced throughout the plan.

## Versioning

`schemaVersion` (e.g. `"1.0"`) is the version of *this schema*. `version` (semver) is the
version of a *specific app's* definition, bumped on every published change — it is what
the promotion pipeline diffs to move dev→prod.

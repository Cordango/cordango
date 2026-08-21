# Budget Planner semantic source comparison

This is the **complete hand-built Budget Planner**, projected into the two source formats being
considered for Cord OSS:

- [`yaml/`](yaml/) - strict declarative `.cord.yaml` files;
- [`typescript/`](typescript/) - typed-looking `.cord.ts` modules using a proposed `@cord/sdk`.

The authority is [`exploration/budget-planner.appdef.json`](../../../exploration/budget-planner.appdef.json),
not the smaller Budget Model used by `CordHandAuthoredAppsTests`. `coverage.json` records the authority
hash, a shared semantic-graph hash, and the counts both variants must preserve.

**The specimen reconstructs the authority exactly** — every value and every array order — and
[`verify.py`](verify.py) is what proves it. That check did not exist until 2026-08-13, and adding it
found four defects the hand-authored tree had shipped with; they are listed under
[Defects this found](#defects-this-found) because each one is a lesson `CordSource` has to carry.

## What "full" means here

Both trees contain the same:

- 11 entities and all 159 fields;
- all computed expressions, rollups, ordered-series calculations, relationships, forms, and record
  detail definitions;
- 3 lifecycles with 15 transitions, with their command behavior folded into the transition aggregate;
- 3 standalone actions, accounting for all 18 original commands;
- 6 automations, including scheduled and field-triggered work;
- 3 roles and their grants;
- 12 named collection views;
- 10 hand-designed screens;
- application presentation, theme, archetype, and 9 explicit relationships.

The original fixture contains no seed rows, so these examples do not invent any. A future
`seed/development.cord.yaml` or `.cord.ts` should be judged separately as installation data rather than
quietly presented as part of the manually authored app.

## Domain and presentation are separate files

An App Definition entity carries three record LAYOUTS alongside its data model — `detail` (the record
page), `peek` (the compact side-panel quick look) and `form` (create/edit). The schema itself says these
are presentation: `kind` is documented as *"how this entity is presented, distinct from its data
shape"*.

They used to live inside the entity file, where they were **32% of its bytes and half of
`scenario.cord.yaml`'s 632 lines** — and where nobody looked for them. Somebody went hunting for the
scenario detail view, did not find it under `views/`, and concluded it was missing. It was not; it was
filed under the data model.

So an entity file is now domain only, and its surfaces live under `views/entities/<key>/`:

```text
entities/scenario.cord.yaml                 fields, relations, calculations   (276 lines)
views/entities/scenario/detail.cord.yaml    hub, tiles, the tab shell
views/entities/scenario/tabs/*.cord.yaml    one file per tab                  (8 for scenario)
views/entities/scenario/peek.cord.yaml
views/entities/scenario/form.cord.yaml
```

`icon` and `kind` stay with the entity: `kind` is navigation placement (`collection` / `config` /
`settings`) and `icon` is one identity scalar every surface reuses, so neither belongs to a particular
layout file.

**Tabs are files because Cord already models a tab as an aggregate** — `CordAggregateKinds.Tab`, keyed
`<screen>/<tab>`, with its own `upsert_screen_tab` and `remove_screen_tab` operations — and the
co-creation loop reviews one tab at a time. Splitting them makes the file layout agree with the
aggregate model rather than contradict it. The `tabs` block keeps its position among its siblings and
its tab order; only the bodies move out.

## Defects this found

Each of these shipped in the hand-authored tree, each is silent, and each is now impossible because the
generator handles it:

| Defect | Why it happened |
|---|---|
| `on: record.created` became the boolean `true` in all six automations | The emitter guarded ambiguous YAML tokens in *values* but its key renderer had a separate rule that let `on` through bare. Now spelled `trigger:`, which is never ambiguous — better than a name that has to be escaped correctly. |
| `unit:  yr` lost its leading space in three fields | A plain YAML scalar is stripped on the way back in. The author typed the space and the authority still carries `" yr"`. Now quoted whenever a scalar has leading or trailing whitespace. |
| Entity and page **order** was recorded nowhere | A directory has no order and a Git tree has none either, but array order drives navigation and the shell, and `DefinitionHash` covers it. A round trip rebuilt every value and opened the app on `categories_config`. Now an explicit `order:` block in `app.cord.yaml`. |
| `kind: board` for `type: kanban` | A rename that collided with `board`, which is already a block kind, so one word meant two things depending on the file. Now `kanban` verbatim. |

The first two are the interesting ones for the format decision: **both are quiet.** The file parses
cleanly and means something other than what was written. They are also both artifacts of hand-typing —
a generated writer quotes by construction, which is the argument for `CordSource` owning the bytes.

## An honest screen caveat

The current Cord screen vocabulary (`list`, `metric`, `chart`, `text`) cannot represent the full
hand-designed Budget Planner exactly. The detailed record surfaces and screens therefore retain an
explicit `layout` tree in both variants. That is intentional evidence for the format decision: it shows
where a friendly semantic source still needs either richer patterns or a deliberate advanced layout
escape hatch. Nothing was dropped to make the examples look cleaner.

These files are **design specimens**, not inputs accepted by today's runtime. `CordSource`, the YAML
profile, and `@cord/sdk` do not exist yet.

## Layout

Each variant is a complete one-app workspace:

```text
<variant>/
|-- cord.yaml or cord.config.ts
`-- apps/
    `-- budget-planner/
        |-- app.cord.yaml or app.cord.ts
        |-- entities/
        |-- workflows/
        |   |-- lifecycles/
        |   |-- actions/
        |   `-- automations/
        |-- roles/
        `-- views/
            |-- collections/     named collection views (tables, boards, timelines)
            |-- entities/        per-entity record surfaces: detail, its tabs, peek, form
            `-- screens/         hand-designed pages
```

The split is aggregate-level: one entity, lifecycle, action, automation, role, collection view, record
surface, tab, or screen per file. Fields remain with their entity; transitions remain with their
lifecycle. This matches the granularity already chosen for Cord operations.

## The name mapping

Semantic source is not the App Definition with different indentation — it renames things where the
friendlier name is genuinely clearer. The complete list, which is the specification `CordSource` has to
implement, lives as data at the top of [`verify.py`](verify.py):

| App Definition | Semantic source | Where |
|---|---|---|
| `key` | `entity` / `screen` / `view` / `role` / `action` / `automation` / `lifecycle` / `app` / `tab` | the file's identity line |
| `labelPlural` | `plural` | entity |
| `displayField` | `display` | entity |
| `computed.expr` | `calculate.expression` | field |
| `computed.rollup` | `calculate.aggregate` | field |
| `type` / `config` | `kind` / `settings` | collection view |
| `blocks` / `entity` | `layout` / `subject` | screen |
| `group` / `navSource` | `navigationGroup` / `navigationSource` | screen |
| `initialState` | `initial` | lifecycle |
| `trigger.event` | `trigger` (with `entity` / `field` / `cron` hoisted beside it) | automation |
| `states[]` / `transitions[]` / `grants[]` / `fields[]` / `relations[]` | maps keyed by key | everywhere |

Two restructurings go further than renaming. A **command folds into the transition that fires it** —
one thing a reader reasons about, rather than a button in one file and a state change in another; the
command's own `label` reappears as `action.label` only when it differs from the transition's, and
`action.key` only when the command is not named after its transition. And a **record surface leaves its
entity** for `views/entities/`, per the section above.

## Regeneration and parity

[`generate.mjs`](generate.mjs) performs a deterministic projection with no npm dependencies:

```text
node examples/semantic/budgetPlanner/generate.mjs
node examples/semantic/budgetPlanner/generate.mjs --check
python examples/semantic/budgetPlanner/verify.py
```

`--check` rebuilds both variants in memory and fails if a generated file is missing or differs. The
generator also refuses count mismatches between the authoritative AppDefinition and the semantic graph.

`verify.py` asks the harder question, and the two are not substitutes. `--check` asks whether the files
on disk are the ones the generator would write — it would happily bless a generator that loses
information, and it did. `verify.py` goes the other way, rebuilding the App Definition **from** the
YAML and requiring it to equal the authority in both values and array order. Run it after any change to
the format, the generator, or the authority.

The generator is comparison scaffolding, not a proposed production compiler. The future production
compiler must parse either source into `CordApp`, then use the existing pure `CordLower`, Gate, and
AppCompiler pipeline.

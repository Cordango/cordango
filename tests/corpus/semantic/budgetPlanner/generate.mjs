// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

import { createHash } from "node:crypto";
import { existsSync, mkdirSync, readFileSync, readdirSync, statSync, writeFileSync } from "node:fs";
import { dirname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
// The authority sits beside the specimen in the corpus: tests/corpus/budget-planner.appdef.json,
// with the specimen at tests/corpus/semantic/budgetPlanner/. Two levels up, same directory.
const repository = resolve(here, "../..");
const authorityPath = join(repository, "budget-planner.appdef.json");
const authorityText = readFileSync(authorityPath, "utf8");
const definition = JSON.parse(authorityText);
const expected = new Map();

const clone = (value) => structuredClone(value);
const without = (value, names) => Object.fromEntries(
  Object.entries(value).filter(([key]) => !names.includes(key)),
);
const compact = (value) => Object.fromEntries(
  Object.entries(value).filter(([, item]) => item !== undefined && item !== null),
);

function keyed(items, convert = (item) => without(item, ["key"])) {
  const result = {};
  for (const item of items ?? []) {
    if (!item?.key) throw new Error("A keyed semantic aggregate has no key");
    if (Object.hasOwn(result, item.key)) throw new Error(`Duplicate semantic key: ${item.key}`);
    result[item.key] = convert(clone(item));
  }
  return result;
}

function semanticField(field) {
  const copy = clone(field);
  delete copy.key;
  const computed = copy.computed;
  delete copy.computed;
  if (computed?.expr !== undefined) {
    copy.calculate = { expression: computed.expr };
  } else if (computed?.rollup !== undefined) {
    copy.calculate = { aggregate: clone(computed.rollup) };
  } else if (computed !== undefined) {
    copy.calculate = clone(computed);
  }
  return copy;
}

// The three RECORD SURFACES the schema defines on an entity: the record page, the compact
// side-panel peek, and the create/edit form. They are presentation, not domain, and they used
// to sit inside the entity file — where they were 32% of its bytes and half of scenario's 632
// lines, and where nobody looked for them. They live under views/ now.
const SURFACES = ["detail", "peek", "form"];

function semanticEntity(entity) {
  const copy = clone(entity);
  const key = copy.key;
  const fields = copy.fields ?? [];
  delete copy.key;
  delete copy.fields;
  for (const surface of SURFACES) delete copy[surface];
  if (copy.labelPlural !== undefined) {
    copy.plural = copy.labelPlural;
    delete copy.labelPlural;
  }
  if (copy.displayField !== undefined) {
    copy.display = copy.displayField;
    delete copy.displayField;
  }
  // `icon` and `kind` STAY. `kind` is navigation placement (collection/config/settings) and
  // `icon` is one identity scalar every surface reuses, so neither belongs to a layout file.
  return { key, ...copy, fields: keyed(fields, semanticField) };
}

/**
 * One entity's record surfaces, as their own aggregates.
 *
 * A detail's tabs become files of their own beside it, because Cord already models a tab as an
 * addressable aggregate (`CordAggregateKinds.Tab`, keyed `<screen>/<tab>`, with its own upsert
 * and remove operations) and the co-creation loop reviews one tab at a time. Splitting them
 * makes the file layout agree with the aggregate model instead of contradicting it: scenario's
 * detail is one 5,900-character blob inline, or eight readable files.
 *
 * The `tabs` block keeps its POSITION among its siblings and its tab ORDER — only the bodies
 * move out. A directory has neither, so that list is load-bearing.
 */
function semanticSurfaces(entity) {
  const result = [];
  for (const surface of SURFACES) {
    if (entity[surface] === undefined) continue;
    const body = clone(entity[surface]);
    for (const block of body.blocks ?? []) {
      if (block.kind !== "tabs") continue;
      const names = [];
      for (const tab of block.tabs ?? []) {
        result.push({ entity: entity.key, kind: "tab", key: tab.key, document: clone(tab) });
        names.push(tab.key);
      }
      block.tabs = names;
    }
    result.push({ entity: entity.key, kind: surface, key: entity.key, document: body });
  }
  return result;
}

const commands = new Map((definition.commands ?? []).map((command) => [command.key, clone(command)]));
const foldedCommands = new Set();

function semanticLifecycle(process) {
  const result = {
    key: process.key,
    entity: process.entity,
    stateField: process.stateField,
    initial: process.initialState,
    states: keyed(process.states),
    transitions: {},
  };

  for (const source of process.transitions ?? []) {
    const transition = clone(source);
    const key = transition.key;
    const commandKey = transition.command;
    delete transition.key;
    delete transition.command;

    if (commandKey) {
      const command = commands.get(commandKey);
      if (!command) throw new Error(`Transition ${key} names missing command ${commandKey}`);
      foldedCommands.add(commandKey);
      const action = without(command, ["key", "entity"]);
      if (action.label === transition.label) delete action.label;
      if (commandKey !== key) action.key = commandKey;
      transition.action = action;
    }
    result.transitions[key] = transition;
  }
  return result;
}

function semanticAction(command) {
  return clone(command);
}

function semanticAutomation(workflow) {
  const copy = clone(workflow);
  const trigger = copy.trigger ?? {};
  delete copy.trigger;
  // `trigger:`, not `on:`. Quoting would make a bare `on` safe, but `"on": record.created`
  // reads badly and the trap comes back the moment somebody hand-edits the file. A name that
  // is never ambiguous is better than a name that is escaped correctly.
  const triggerRest = without(trigger, ["event"]);
  return compact({
    key: copy.key,
    name: copy.name,
    trigger: trigger.event,
    ...triggerRest,
    when: copy.when,
    effects: copy.effects,
    ...without(copy, ["key", "name", "when", "effects"]),
  });
}

function semanticRole(role) {
  const copy = clone(role);
  const grants = copy.grants ?? [];
  delete copy.grants;
  const byEntity = {};
  for (const grant of grants) {
    if (!grant.entity) throw new Error(`Role ${role.key} has a grant without an entity`);
    if (Object.hasOwn(byEntity, grant.entity))
      throw new Error(`Role ${role.key} grants ${grant.entity} more than once`);
    byEntity[grant.entity] = without(grant, ["entity"]);
  }
  return { ...copy, grants: byEntity };
}

function semanticCollectionView(view) {
  return {
    key: view.key,
    label: view.label,
    entity: view.entity,
    // `kanban` verbatim. Renaming it to `board` collided with the `board` BLOCK kind, so one
    // word meant two things depending on which file it appeared in.
    kind: view.type,
    settings: clone(view.config ?? {}),
    ...without(view, ["key", "label", "entity", "type", "config"]),
  };
}

function semanticScreen(page) {
  return compact({
    key: page.key,
    label: page.label,
    icon: page.icon,
    subject: page.entity,
    navigationGroup: page.group,
    navigationSource: page.navSource,
    detailFull: page.detailFull,
    layout: clone(page.blocks ?? []),
    ...without(page, ["key", "label", "icon", "entity", "group", "navSource", "detailFull", "blocks"]),
  });
}

function semanticApp() {
  const excluded = ["key", "name", "version", "schemaVersion", "description", "entities", "processes",
    "commands", "workflows", "roles", "views", "pages", "relations"];
  return compact({
    key: definition.key,
    name: definition.name,
    version: definition.version,
    schemaVersion: definition.schemaVersion,
    description: definition.description,
    ...without(clone(definition), excluded),
    relations: keyed(definition.relations ?? []),
  });
}

const app = semanticApp();
const entities = (definition.entities ?? []).map(semanticEntity);
const surfaces = (definition.entities ?? []).flatMap(semanticSurfaces);
const lifecycles = (definition.processes ?? []).map(semanticLifecycle);
const actions = (definition.commands ?? [])
  .filter((command) => !foldedCommands.has(command.key))
  .map(semanticAction);
const automations = (definition.workflows ?? []).map(semanticAutomation);
const roles = (definition.roles ?? []).map(semanticRole);
const collectionViews = (definition.views ?? []).map(semanticCollectionView);
const screens = (definition.pages ?? []).map(semanticScreen);

if (foldedCommands.size + actions.length !== (definition.commands ?? []).length)
  throw new Error("Not every command was represented as a lifecycle transition or standalone action");

/*
 * ORDER.
 *
 * A set of files has no order and a Git tree has none either, but array order is meaningful in
 * an App Definition and `DefinitionHash` covers it: entity order drives navigation, page order
 * drives the shell. Without this block a round trip through the tree reconstructs every value
 * correctly and silently reorders the application — the first page becomes whichever key sorts
 * first alphabetically. It is recorded once, here, rather than as a field on every file.
 */
app.order = {
  entities: entities.map((item) => item.key),
  pages: screens.map((item) => item.key),
  views: collectionViews.map((item) => item.key),
  roles: roles.map((item) => item.key),
  processes: lifecycles.map((item) => item.key),
  commands: (definition.commands ?? []).map((command) => command.key),
  workflows: automations.map((item) => item.key),
};

const workspace = {
  formatVersion: 1,
  workspaceId: "semantic-budget-planner-sample",
  name: "Budget Planner semantic sample",
  runtime: ">=0.1 <0.2",
  coreApps: "default",
  apps: ["apps/budget-planner"],
};

const semantic = {
  workspace,
  app,
  entities,
  lifecycles,
  actions,
  automations,
  roles,
  collectionViews,
  screens,
};

const counts = {
  entities: entities.length,
  fields: entities.reduce((sum, entity) => sum + Object.keys(entity.fields).length, 0),
  lifecycles: lifecycles.length,
  transitions: lifecycles.reduce((sum, lifecycle) => sum + Object.keys(lifecycle.transitions).length, 0),
  standaloneActions: actions.length,
  commandsRepresented: foldedCommands.size + actions.length,
  automations: automations.length,
  roles: roles.length,
  collectionViews: collectionViews.length,
  screens: screens.length,
  relations: Object.keys(app.relations ?? {}).length,
  recordSurfaces: surfaces.filter((item) => item.kind !== "tab").length,
  detailTabs: surfaces.filter((item) => item.kind === "tab").length,
};

const sourceCounts = {
  entities: definition.entities?.length ?? 0,
  fields: (definition.entities ?? []).reduce((sum, entity) => sum + (entity.fields?.length ?? 0), 0),
  lifecycles: definition.processes?.length ?? 0,
  transitions: (definition.processes ?? []).reduce((sum, process) => sum + (process.transitions?.length ?? 0), 0),
  commandsRepresented: definition.commands?.length ?? 0,
  automations: definition.workflows?.length ?? 0,
  roles: definition.roles?.length ?? 0,
  collectionViews: definition.views?.length ?? 0,
  screens: definition.pages?.length ?? 0,
  relations: definition.relations?.length ?? 0,
};

for (const [key, value] of Object.entries(sourceCounts)) {
  if (counts[key] !== value)
    throw new Error(`Semantic coverage mismatch for ${key}: ${counts[key]} != ${value}`);
}

// Tokens YAML 1.1 resolves to something other than a string. `on`/`off`/`yes`/`no` are the
// famous ones; a bare `2.0` is a float and a bare `#` starts a comment.
const AMBIGUOUS = /^(?:|null|true|false|yes|no|on|off|~|[-+]?\d+(?:\.\d+)?|\d{4}-\d{2}-\d{2})$/i;

function yamlScalar(value) {
  if (value === null) return "null";
  if (typeof value === "boolean" || typeof value === "number") return String(value);
  const text = String(value);
  if (text.includes("\n")) return JSON.stringify(text);
  const unsafe = /^[\-?:,\[\]{}#&*!|>'"%@`]/.test(text)
    || text.includes(": ") || text.includes(" #") || /[\r\t]/.test(text)
    // LEADING OR TRAILING WHITESPACE. A plain scalar is stripped on the way back in, so
    // `unit:  yr` round-trips as "yr" and the space the author typed is gone without a word.
    // It shipped that way: the authority says " yr" and " mo" for three scenario fields.
    || text !== text.trim();
  return AMBIGUOUS.test(text) || unsafe ? JSON.stringify(text) : text;
}

const scalar = (value) => value === null || ["string", "number", "boolean"].includes(typeof value);

function yaml(value, indent = 0) {
  const pad = " ".repeat(indent);
  if (scalar(value)) return `${pad}${yamlScalar(value)}`;
  if (Array.isArray(value)) {
    if (value.length === 0) return `${pad}[]`;
    return value.map((item) => {
      if (scalar(item)) return `${pad}- ${yamlScalar(item)}`;
      if (!Array.isArray(item)) {
        const rendered = yaml(item, indent + 2).split("\n");
        return `${pad}- ${rendered[0].trimStart()}${rendered.length > 1 ? `\n${rendered.slice(1).join("\n")}` : ""}`;
      }
      return `${pad}-\n${yaml(item, indent + 2)}`;
    }).join("\n");
  }
  const entries = Object.entries(value);
  if (entries.length === 0) return `${pad}{}`;
  return entries.map(([key, item]) => {
    // A KEY needs the same guard a value does, and used not to have it: `on` matches the
    // identifier pattern, so `on: record.created` was emitted bare and parsed back as the
    // boolean true. Every automation file silently lost its trigger that way.
    const renderedKey = /^[A-Za-z_][A-Za-z0-9_-]*$/.test(key) && !AMBIGUOUS.test(key)
      ? key : JSON.stringify(key);
    if (scalar(item)) return `${pad}${renderedKey}: ${yamlScalar(item)}`;
    if (Array.isArray(item) && item.length === 0) return `${pad}${renderedKey}: []`;
    if (!Array.isArray(item) && Object.keys(item).length === 0) return `${pad}${renderedKey}: {}`;
    return `${pad}${renderedKey}:\n${yaml(item, indent + 2)}`;
  }).join("\n");
}

function yamlDocument(kind, value) {
  const copy = clone(value);
  const key = copy.key;
  delete copy.key;
  return `${yaml({ [kind]: key, ...copy })}\n`;
}

const identifier = (key) => /^[A-Za-z_$][A-Za-z0-9_$]*$/.test(key);

function typescript(value, indent = 0) {
  const pad = " ".repeat(indent);
  if (value === null || typeof value === "boolean" || typeof value === "number") return String(value);
  if (typeof value === "string") return JSON.stringify(value);
  if (Array.isArray(value)) {
    if (value.length === 0) return "[]";
    if (value.every(scalar) && JSON.stringify(value).length <= 88)
      return `[${value.map((item) => typescript(item)).join(", ")}]`;
    return `[\n${value.map((item) => `${" ".repeat(indent + 2)}${typescript(item, indent + 2)},`).join("\n")}\n${pad}]`;
  }
  const entries = Object.entries(value);
  if (entries.length === 0) return "{}";
  const body = entries.map(([key, item]) => {
    const renderedKey = identifier(key) ? key : JSON.stringify(key);
    return `${" ".repeat(indent + 2)}${renderedKey}: ${typescript(item, indent + 2)},`;
  }).join("\n");
  return `{\n${body}\n${pad}}`;
}

function tsModule(builder, value) {
  return `import { ${builder} } from "@cord/sdk";\n\nexport default ${builder}(${typescript(value)} as const);\n`;
}

function assertTypescriptSyntax(path, content) {
  const executable = content
    .replace(/^import \{ [A-Za-z_$][A-Za-z0-9_$]* \} from "@cord\/sdk";\n\nexport default [A-Za-z_$][A-Za-z0-9_$]*\(/, "return (")
    .replace(/ as const\);\n$/, ");");
  if (executable === content) throw new Error(`Could not prepare ${path} for syntax validation`);
  try {
    Function(executable);
  } catch (error) {
    throw new Error(`Invalid generated TypeScript in ${path}: ${error.message}`, { cause: error });
  }
}

function add(path, content) {
  const normalizedPath = path.replaceAll("\\", "/");
  const normalizedContent = content.endsWith("\n") ? content : `${content}\n`;
  if (normalizedPath.endsWith(".ts")) assertTypescriptSyntax(normalizedPath, normalizedContent);
  expected.set(normalizedPath, normalizedContent);
}

function addAggregate(base, item, yamlKind, builder) {
  add(`yaml/${base}/${item.key}.cord.yaml`, yamlDocument(yamlKind, item));
  add(`typescript/${base}/${item.key}.cord.ts`, tsModule(builder, item));
}

add("yaml/cord.yaml", `${yaml(workspace)}\n`);
add("typescript/cord.config.ts", tsModule("defineWorkspace", workspace));
add("yaml/apps/budget-planner/app.cord.yaml", yamlDocument("app", app));
add("typescript/apps/budget-planner/app.cord.ts", tsModule("defineApp", app));

for (const item of entities)
  addAggregate("apps/budget-planner/entities", item, "entity", "defineEntity");
for (const item of lifecycles)
  addAggregate("apps/budget-planner/workflows/lifecycles", item, "lifecycle", "defineLifecycle");
for (const item of actions)
  addAggregate("apps/budget-planner/workflows/actions", item, "action", "defineAction");
for (const item of automations)
  addAggregate("apps/budget-planner/workflows/automations", item, "automation", "defineAutomation");
for (const item of roles)
  addAggregate("apps/budget-planner/roles", item, "role", "defineRole");
for (const item of collectionViews)
  addAggregate("apps/budget-planner/views/collections", item, "view", "defineCollectionView");
for (const item of screens)
  addAggregate("apps/budget-planner/views/screens", item, "screen", "defineScreen");

// The record surfaces, under the entity they belong to rather than inside it.
//   views/entities/scenario/detail.cord.yaml
//   views/entities/scenario/tabs/projection.cord.yaml
//   views/entities/scenario/peek.cord.yaml
//   views/entities/scenario/form.cord.yaml
const SURFACE_BUILDERS = {
  detail: "defineDetail", peek: "definePeek", form: "defineForm", tab: "defineTab",
};
for (const surface of surfaces) {
  const base = `apps/budget-planner/views/entities/${surface.entity}`;
  const name = surface.kind === "tab" ? `tabs/${surface.key}` : surface.kind;
  const document = { key: surface.key, ...surface.document };
  add(`yaml/${base}/${name}.cord.yaml`, yamlDocument(surface.kind, document));
  add(`typescript/${base}/${name}.cord.ts`, tsModule(SURFACE_BUILDERS[surface.kind], document));
}

const coverage = {
  authority: "../../budget-planner.appdef.json",
  authoritySha256: createHash("sha256").update(authorityText).digest("hex"),
  semanticSha256: createHash("sha256").update(JSON.stringify(semantic)).digest("hex"),
  counts,
  note: "Both variants are generated from the same semantic object graph. Explicit layout is retained where the current Cord screen vocabulary cannot express the hand-designed screen exactly.",
};
add("coverage.json", `${JSON.stringify(coverage, null, 2)}\n`);

function generatedFiles(root, prefix = "") {
  if (!existsSync(root)) return [];
  const result = [];
  for (const name of readdirSync(root)) {
    const full = join(root, name);
    const rel = prefix ? `${prefix}/${name}` : name;
    if (statSync(full).isDirectory()) result.push(...generatedFiles(full, rel));
    else if (name.endsWith(".cord.yaml") || name.endsWith(".cord.ts")
      || rel === "cord.yaml" || rel === "cord.config.ts") result.push(rel);
  }
  return result;
}

const check = process.argv.includes("--check");
if (check) {
  const problems = [];
  for (const [path, content] of expected) {
    const full = join(here, path);
    if (!existsSync(full)) problems.push(`missing ${path}`);
    else if (readFileSync(full, "utf8") !== content) problems.push(`changed ${path}`);
  }
  for (const root of ["yaml", "typescript"]) {
    for (const file of generatedFiles(join(here, root))) {
      const path = `${root}/${file}`;
      if (!expected.has(path)) problems.push(`unexpected ${path}`);
    }
  }
  if (problems.length > 0) {
    console.error(problems.join("\n"));
    process.exitCode = 1;
  } else {
    console.log(`Budget Planner semantic samples are current (${expected.size} generated files).`);
  }
} else {
  for (const [path, content] of expected) {
    const full = join(here, path);
    mkdirSync(dirname(full), { recursive: true });
    writeFileSync(full, content, "utf8");
  }
  console.log(`Generated ${expected.size} files from ${relative(repository, authorityPath)}.`);
}

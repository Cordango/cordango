#!/usr/bin/env python3
# SPDX-License-Identifier: Apache-2.0
# Copyright (c) Cordango and contributors.
# Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
# Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

"""Rebuild the App Definition from the YAML specimen and require it to be IDENTICAL to the
authority — same values AND the same array order.

    python verify.py            # after: node generate.mjs

`node generate.mjs --check` answers a different and weaker question: are the files on disk the
ones the generator would write. This answers whether the FORMAT can carry the application at
all, by going the other way. Both matter, and only this one would have caught the four defects
the hand-authored tree shipped with:

  * `on: record.created` — a bare `on` key is the boolean true in YAML 1.1, so all six
    automations silently lost their trigger;
  * `unit:  yr` — a plain scalar is stripped, so three fields lost the leading space the
    author typed and the authority still carries;
  * entity and page ORDER — recorded nowhere, so a round trip reconstructed every value and
    opened the app on a different page;
  * `kind: board` for `type: kanban` — a rename that collided with the `board` block kind.

It is also the spec `CordSource` has to satisfy: the mapping applied below IS the name mapping,
and if this script can rebuild the authority then a C# reader doing the same thing can too.
"""
import json
import pathlib
import sys

try:
    import yaml
except ImportError:
    sys.exit('verify.py needs PyYAML:  pip install pyyaml')

HERE = pathlib.Path(__file__).resolve().parent
# The authority sits beside the specimen in the corpus: tests/corpus/budget-planner.appdef.json,
# with the specimen at tests/corpus/semantic/budgetPlanner/. Two levels up, same directory.
AUTHORITY = HERE.parents[1] / 'budget-planner.appdef.json'
ROOT = HERE / 'yaml' / 'apps' / 'budget-planner'

# ---- the name mapping, in one place ---------------------------------------------------------
#
# Every rename between the App Definition and the semantic source. Kept as data rather than
# scattered through the code because it is the artifact `CordSource` has to implement, and a
# mapping spread over twenty branches is one that drifts.
ENTITY_NAMES = {'key': 'entity', 'labelPlural': 'plural', 'displayField': 'display'}
VIEW_NAMES = {'key': 'view', 'type': 'kind', 'config': 'settings'}
PAGE_NAMES = {'key': 'screen', 'blocks': 'layout', 'entity': 'subject',
              'group': 'navigationGroup', 'navSource': 'navigationSource'}
SURFACES = ('detail', 'peek', 'form')


def read(rel):
    path = ROOT / rel
    return yaml.safe_load(path.read_text(encoding='utf-8')) if path.exists() else None


def invert(mapping):
    return {v: k for k, v in mapping.items()}


def unkey(mapping, name):
    """A YAML map-of-things back to a list-of-things with the key inlined, in file order."""
    return [{name: k, **(v or {})} for k, v in (mapping or {}).items()]


def field_of(key, y):
    out = {'key': key}
    for k, v in y.items():
        if k == 'calculate':
            computed = {}
            if 'expression' in v:
                computed['expr'] = v['expression']
            if 'aggregate' in v:
                computed['rollup'] = v['aggregate']
            out['computed'] = computed or v
        else:
            out[k] = v
    return out


def entity_of(key):
    y = read(f'entities/{key}.cord.yaml')
    names = invert(ENTITY_NAMES)
    e = {}
    for k, v in y.items():
        if k == 'fields':
            e['fields'] = [field_of(fk, fv) for fk, fv in v.items()]
        else:
            e[names.get(k, k)] = v

    # The record surfaces live under views/ now; re-attach them, and re-inline a detail's tabs.
    for surface in SURFACES:
        y2 = read(f'views/entities/{key}/{surface}.cord.yaml')
        if y2 is None:
            continue
        body = {k: v for k, v in y2.items() if k != surface}
        for block in body.get('blocks', []) if surface == 'detail' else []:
            if block.get('kind') != 'tabs':
                continue
            block['tabs'] = [
                {'key': (t := read(f'views/entities/{key}/tabs/{name}.cord.yaml'))['tab'],
                 **{k: v for k, v in t.items() if k != 'tab'}}
                for name in block['tabs']]
        e[surface] = body
    return e


def lifecycles():
    """Every lifecycle file as its process, plus the commands its transitions carry."""
    processes, commands = {}, {}
    for f in sorted((ROOT / 'workflows/lifecycles').glob('*.cord.yaml')):
        y = yaml.safe_load(f.read_text(encoding='utf-8'))
        transitions = []
        for tkey, t in (y.get('transitions') or {}).items():
            transition = {'key': tkey, **{k: v for k, v in t.items() if k != 'action'}}
            if (action := t.get('action')) is not None:
                # `action.key` appears only when the command is not named after its transition.
                ckey = action.get('key', tkey)
                transition['command'] = ckey
                commands[ckey] = {'key': ckey, 'entity': y['entity'],
                                  'label': t.get('label'),
                                  **{k: v for k, v in action.items() if k != 'key'}}
            transitions.append(transition)
        processes[y['lifecycle']] = {
            'key': y['lifecycle'], 'entity': y['entity'], 'stateField': y.get('stateField'),
            'initialState': y.get('initial'), 'states': unkey(y.get('states'), 'key'),
            'transitions': transitions}

    for f in sorted((ROOT / 'workflows/actions').glob('*.cord.yaml')):
        y = yaml.safe_load(f.read_text(encoding='utf-8'))
        commands[y['action']] = {'key': y['action'],
                                 **{k: v for k, v in y.items() if k != 'action'}}
    return processes, commands


def build():
    app = read('app.cord.yaml')
    order = app['order']
    doc = {}
    for k, v in app.items():
        if k == 'order':
            continue
        doc['key' if k == 'app' else k] = unkey(v, 'key') if k == 'relations' else v

    processes, commands = lifecycles()
    view_names, page_names = invert(VIEW_NAMES), invert(PAGE_NAMES)

    doc['entities'] = [entity_of(k) for k in order['entities']]
    doc['views'] = [{view_names.get(k, k): v
                     for k, v in read(f'views/collections/{key}.cord.yaml').items()}
                    for key in order['views']]
    doc['pages'] = [{page_names.get(k, k): v
                     for k, v in read(f'views/screens/{key}.cord.yaml').items()}
                    for key in order['pages']]
    doc['roles'] = [{('key' if k == 'role' else k): (unkey(v, 'entity') if k == 'grants' else v)
                     for k, v in read(f'roles/{key}.cord.yaml').items()}
                    for key in order['roles']]
    doc['processes'] = [processes[k] for k in order['processes']]
    doc['commands'] = [commands[k] for k in order['commands']]

    workflows = []
    for key in order['workflows']:
        y = read(f'workflows/automations/{key}.cord.yaml')
        w, trigger = {'key': y['automation']}, {}
        for k, v in y.items():
            if k == 'automation':
                continue
            if k == 'trigger':
                trigger['event'] = v
            elif k in ('entity', 'field', 'cron'):
                trigger[k] = v
            else:
                w[k] = v
        w['trigger'] = trigger
        workflows.append(w)
    doc['workflows'] = workflows
    return doc


def canon(x):
    """Sort object keys — a JSON object is unordered — and leave every LIST exactly as built,
    because array order is meaningful and is half of what this script checks."""
    if isinstance(x, dict):
        return {k: canon(v) for k, v in sorted(x.items()) if v is not None}
    if isinstance(x, list):
        return [canon(i) for i in x]
    return x


def diff(a, b, path=''):
    out = []
    if isinstance(a, dict) and isinstance(b, dict):
        for k in sorted(set(a) | set(b)):
            if k not in a:
                out.append(f'{path}/{k}: only in the specimen')
            elif k not in b:
                out.append(f'{path}/{k}: only in the authority')
            else:
                out += diff(a[k], b[k], f'{path}/{k}')
    elif isinstance(a, list) and isinstance(b, list):
        if len(a) != len(b):
            out.append(f'{path}: authority has {len(a)}, specimen has {len(b)}')
        for i, (x, y) in enumerate(zip(a, b)):
            label = x.get('key', i) if isinstance(x, dict) else i
            out += diff(x, y, f'{path}[{label}]')
    elif a != b:
        out.append(f'{path}: authority={json.dumps(a)[:80]}  specimen={json.dumps(b)[:80]}')
    return out


def main():
    if not ROOT.exists():
        sys.exit(f'no specimen at {ROOT} — run `node generate.mjs` first')

    problems = diff(canon(json.loads(AUTHORITY.read_text(encoding='utf-8'))), canon(build()))
    if not problems:
        print('IDENTICAL - values and array order both reconstruct exactly.')
        return 0

    print(f'{len(problems)} difference(s) between the specimen and the authority:\n')
    for p in problems[:60]:
        print('  ' + p)
    if len(problems) > 60:
        print(f'  ... and {len(problems) - 60} more')
    return 1


sys.exit(main())

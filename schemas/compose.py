#!/usr/bin/env python3
# SPDX-License-Identifier: Apache-2.0
# Copyright (c) Cordango and contributors.
# Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
# Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

"""Compose app-definition.schema.json from the per-concern source files in src/.

The composed file is the ONLY artifact anything downstream consumes (embedded
resource in AppBuilder.Definition, validate.py, the examples/tests) and it stays
checked in. Workflow: edit the src/ files, run this script, commit both; the api
container must be restarted afterwards (the embedded schema cannot hot-reload).

    python compose.py                  rewrite app-definition.schema.json
    python compose.py --check          exit 1 if the checked-in file differs from
                                       what src/ composes to (CI drift guard)
    python compose.py --verify-old F   semantic equality report against an older
                                       composed file (key order ignored) -- used
                                       for the one-time monolith migration

Rules:
- src/root.json is the envelope: every keyword except properties/$defs is taken
  from it verbatim, in its order.
- Every source file contributes `properties` and `$defs`. The same key defined
  in two files is a hard error.
- Def bodies are copied VERBATIM -- the composer never transforms them. In
  particular the block/formBlock/effect unions keep their allOf if/then const
  dispatch; `unevaluatedProperties` must never appear (it crashes the
  JsonSchema.Net test host) and is asserted below.
- MANIFEST is the ownership record (which concern owns which key). A key found
  in a source file but not in the manifest, or vice versa, is a hard error, so
  concerns cannot silently leak between files. Adding a def = add it to the
  right src file AND its manifest entry.
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
SRC = HERE / "src"
OUT = HERE / "app-definition.schema.json"

# Fixed composition order. connectivity.json joins in a later phase.
FILES = [
    "root.json",
    "domain.json",
    "ui.json",
    "behavior.json",
    "notifications.json",
    "security.json",
]

BLOCK_KINDS = [
    "view", "tabs", "section", "columns", "widgets", "fields", "child",
    "settings", "hub", "tiles", "process", "stack", "row", "grid", "card",
    "repeat", "cell", "stat", "field", "text", "chip", "avatar", "progress",
    "chart", "action", "create", "control", "filterbar", "timeline", "table", "calendar", "form",
    "split", "orgchart", "board", "gantt", "history", "answers", "intake",
    "externalEmbed", "relatedApps",
]

MANIFEST: dict[str, dict[str, list[str]]] = {
    "root.json": {
        "properties": ["schemaVersion", "key", "name", "version", "description",
                       "purpose", "weekStart", "archetype", "uses", "plugins"],
        "defs": ["visibleWhen", "identifier", "fieldPath", "hexColor", "filter",
                 "appDependency", "pluginRef"],
    },
    "domain.json": {
        "properties": ["entities", "relations"],
        "defs": ["entity", "field", "relation"],
    },
    "ui.json": {
        "properties": ["presentation", "views", "pages", "theme"],
        "defs": ["presentation", "formBlock", "sort", "blockSource", "filterBar",
                 "blockSearch", "groupBy", "view", "page", "screenState", "block", "deepLink",
                 "tile", "tab", "theme", "combine", "statMax", "timeAxis"]
                + [f"block_{k}" for k in BLOCK_KINDS],
    },
    "behavior.json": {
        "properties": ["processes", "commands", "workflows"],
        "defs": ["workflow", "condition", "effect", "effect_updateRecord", "effect_deleteRecord",
                 "effect_createRecord", "effect_createForEach", "effect_webhook", "effect_enrich", "command", "process"],
    },
    "notifications.json": {
        "properties": [],
        "defs": ["effect_notify", "effect_email"],
    },
    "security.json": {
        "properties": ["roles"],
        "defs": ["role"],
    },
}


def fail(msg: str) -> None:
    print(f"compose.py: ERROR: {msg}", file=sys.stderr)
    sys.exit(1)


def load(name: str) -> dict:
    path = SRC / name
    if not path.exists():
        fail(f"missing source file {path}")
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def compose() -> dict:
    if set(FILES) != set(MANIFEST):
        fail("FILES and MANIFEST disagree")
    props: dict = {}
    defs: dict = {}
    envelope: dict = {}
    for name in FILES:
        doc = load(name)
        fprops = doc.get("properties", {})
        fdefs = doc.get("$defs", {})
        if name == "root.json":
            envelope = {k: v for k, v in doc.items()
                        if k not in ("properties", "$defs")}
        else:
            extra = set(doc) - {"properties", "$defs"}
            if extra:
                fail(f"{name}: only properties/$defs allowed, found {sorted(extra)}")
        want = MANIFEST[name]
        if list(fprops) != want["properties"]:
            fail(f"{name}: properties {list(fprops)} != manifest {want['properties']}")
        if list(fdefs) != want["defs"]:
            fail(f"{name}: $defs mismatch with manifest\n  file:     {list(fdefs)}\n  manifest: {want['defs']}")
        for k, v in fprops.items():
            if k in props:
                fail(f"property '{k}' defined in two source files")
            props[k] = v
        for k, v in fdefs.items():
            if k in defs:
                fail(f"$def '{k}' defined in two source files")
            defs[k] = v

    doc = dict(envelope)
    doc["properties"] = props
    doc["$defs"] = defs

    for k in doc.get("required", []):
        if k not in props:
            fail(f"required key '{k}' has no property definition")
    check_refs(doc, defs)
    check_no_unevaluated(doc)
    try:
        from jsonschema import Draft202012Validator
        Draft202012Validator.check_schema(doc)
    except ImportError:
        print("compose.py: warning: jsonschema not installed, check_schema skipped",
              file=sys.stderr)
    return doc


def check_refs(node, defs: dict, path: str = "$") -> None:
    if isinstance(node, dict):
        for k, v in node.items():
            if k == "$ref" and isinstance(v, str):
                if not v.startswith("#/$defs/"):
                    fail(f"{path}: non-local $ref '{v}'")
                if v[len("#/$defs/"):] not in defs:
                    fail(f"{path}: $ref '{v}' does not resolve")
            else:
                check_refs(v, defs, f"{path}.{k}")
    elif isinstance(node, list):
        for i, v in enumerate(node):
            check_refs(v, defs, f"{path}[{i}]")


def check_no_unevaluated(node, path: str = "$") -> None:
    if isinstance(node, dict):
        for k, v in node.items():
            if k == "unevaluatedProperties":
                fail(f"{path}: unevaluatedProperties is forbidden "
                     "(crashes the JsonSchema.Net test host)")
            check_no_unevaluated(v, f"{path}.{k}")
    elif isinstance(node, list):
        for i, v in enumerate(node):
            check_no_unevaluated(v, f"{path}[{i}]")


def render(doc: dict) -> str:
    return json.dumps(doc, indent=2, ensure_ascii=False) + "\n"


def semantic_diff(old: dict, new: dict) -> list[str]:
    """Key-order-insensitive comparison; returns human-readable differences."""
    out: list[str] = []
    for k in sorted(set(old) | set(new) - {"properties", "$defs"}):
        if k in ("properties", "$defs"):
            continue
        if old.get(k) != new.get(k):
            out.append(f"envelope '{k}' differs")
    for section in ("properties", "$defs"):
        o, n = old.get(section, {}), new.get(section, {})
        for k in sorted(set(o) - set(n)):
            out.append(f"{section}/{k} removed")
        for k in sorted(set(n) - set(o)):
            out.append(f"{section}/{k} added")
        for k in sorted(set(o) & set(n)):
            if o[k] != n[k]:
                out.append(f"{section}/{k} differs")
    return out


def main() -> None:
    args = sys.argv[1:]
    doc = compose()
    if args[:1] == ["--check"]:
        current = OUT.read_text(encoding="utf-8") if OUT.exists() else ""
        if current.replace("\r\n", "\n") != render(doc):
            fail(f"{OUT.name} is out of date -- run `python compose.py` and commit the result")
        print("compose.py: up to date")
    elif args[:1] == ["--verify-old"]:
        if len(args) != 2:
            fail("--verify-old needs a file path")
        with open(args[1], encoding="utf-8") as f:
            old = json.load(f)
        diffs = semantic_diff(old, doc)
        if diffs:
            for d in diffs:
                print(f"  {d}", file=sys.stderr)
            fail(f"{len(diffs)} semantic difference(s) vs {args[1]}")
        print("compose.py: semantically identical")
    elif not args:
        with open(OUT, "w", encoding="utf-8", newline="\n") as f:
            f.write(render(doc))
        print(f"compose.py: wrote {OUT.name} "
              f"({len(doc['properties'])} properties, {len(doc['$defs'])} $defs)")
    else:
        fail(f"unknown arguments {args}")


if __name__ == "__main__":
    main()

#!/usr/bin/env python
# SPDX-License-Identifier: Apache-2.0
# Copyright (c) Cordango and contributors.
# Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
# Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

"""Validate an App Definition document against app-definition.schema.json.

Usage:
    python schema/validate.py <path-to-appdef.json> [more.json ...]

Exit code 0 if all documents are valid, 1 otherwise. This is the same gate the
AI validate->repair loop and CI use: an App Definition that does not validate is
never deployed.
"""
import json
import sys
from pathlib import Path

from jsonschema import Draft202012Validator

SCHEMA_PATH = Path(__file__).with_name("app-definition.schema.json")


def validate(doc_path: Path, validator: Draft202012Validator) -> bool:
    doc = json.loads(doc_path.read_text(encoding="utf-8"))
    errors = sorted(validator.iter_errors(doc), key=lambda e: list(e.path))
    if not errors:
        print(f"VALID   {doc_path}")
        return True
    print(f"INVALID {doc_path} ({len(errors)} error(s)):")
    for e in errors:
        loc = "/".join(str(p) for p in e.path) or "<root>"
        print(f"  at [{loc}]: {e.message}")
    return False


def main(argv: list[str]) -> int:
    if not argv:
        print(__doc__)
        return 2
    schema = json.loads(SCHEMA_PATH.read_text(encoding="utf-8"))
    Draft202012Validator.check_schema(schema)
    validator = Draft202012Validator(schema)
    ok = all(validate(Path(a), validator) for a in argv)
    return 0 if ok else 1


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))

// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

import { readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

export const fixtureRoot = join(fileURLToPath(new URL(".", import.meta.url)), "..", "..", "..", "tests", "fixtures");

export function fixtureFiles(area: string): string[] {
  return readdirSync(join(fixtureRoot, area))
    .filter((name) => name.endsWith(".json"))
    .sort();
}

export function loadFixture(area: string, name: string): Record<string, unknown> {
  return JSON.parse(readFileSync(join(fixtureRoot, area, name), "utf8")) as Record<string, unknown>;
}

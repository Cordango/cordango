// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    include: ["test/**/*.test.ts"],
    // PGlite boots a WASM Postgres in-process: ~2.5s cold, more when
    // several test files boot one at once. The boot is paid in beforeAll.
    testTimeout: 15_000,
    hookTimeout: 60_000,
  },
});

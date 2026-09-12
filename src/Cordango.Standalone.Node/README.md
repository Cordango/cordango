# @cordango/standalone

The Cordango standalone runtime for Node — the library a generated `node-vue` application
references. Records, permissions, conditions, computed figures, sign-in, and the HTTP surface a
generated application serves.

Generated applications pin this package at the exact version of the generator that wrote their
`package.json`; the two publish from one git tag and cannot be installed apart.

## What is in it

| | |
| --- | --- |
| **Records** | A descriptor per entity, a store over PostgreSQL, a query layer (filters, sorting, paging), aggregates in decimal arithmetic, and hooks around every write |
| **Permissions** | The definition's roles as data, one resolver, per-field read and write rules, and commands denied unless a grant names them |
| **Commands** | A business action with its own transition, guard, required input and notifications — checked in that order, so a command that will not run has touched nothing |
| **Computed figures** | The same parser and evaluator the hosted platform runs, against the same decision fixtures |
| **Identity** | Password sign-in (scrypt, no native addon), sessions in a signed cookie, access keys for callers that are not browsers, lockout, and a security stamp that revokes everything at once |
| **The wire** | Express routers for records, accounts, administration, the directory, notifications, preferences and media — plus antiforgery, the `{code, error}` refusal shape, and per-language messages |

The **gateway** is the seam that matters: every read and every write goes through it, so a caller
holding an access key reaches exactly what the same person reaches in the browser, and is refused
in the same words. Adding a new way into an application cannot add a new way around its rules.

## What is not in it yet

Workflows and their scheduler, rollups and the recompute cascade, and the public form endpoints.
The generator reports each of these as `CORD23xx` — "not generated yet" — against the application
that asked for it, rather than emitting a column that stays empty.

## Parity

The semantic core — the three-valued computed arithmetic, the condition evaluator, the permission
resolver — is a port of the C# runtime `Cordango.Standalone`, and the two are held together by the
decision fixtures in `tests/fixtures/` of the repository: hand-written (record, question) → answer
cases that every implementation's suite runs. Drift becomes a red test rather than a wrong answer
in production.

## Using it directly

A generated application does this for you, in `server.ts` and `app.ts`. It is not hidden:

```ts
import { CordangoRuntime, addDirectory, createApp, openDatabase, recordsRouter } from "@cordango/standalone";

const runtime = new CordangoRuntime({
  appKey: "expenses",
  appName: "Expenses",
  driver: await openDatabase(process.env.DATABASE_URL),
  secret: process.env.APP_SECRET!,
  permissions: appPermissions,
  commands: appCommands,
});

addDirectory(runtime);
runtime.register({ descriptor: expenseDescriptor });
await runtime.start();

createApp({
  runtime,
  routes: (api) => api.use("/expense", recordsRouter("expense")),
}).listen(5000);
```

`openDatabase` with no connection string is [PGlite](https://pglite.dev): a real PostgreSQL running
inside the process, on the local disk. Both drivers speak the same dialect, so nothing above the
driver knows which it got.

Part of [Cordango](https://github.com/cordango/cordango), the open application language and
compiler. Apache-2.0.

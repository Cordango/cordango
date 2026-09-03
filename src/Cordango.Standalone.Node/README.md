# @cordango/standalone

The Cordango standalone runtime for Node — the library a generated `node-vue` application
references. Records, permissions, conditions, computed figures, and (as the target grows toward
parity with `dotnet-vue`) the HTTP surface a generated application serves.

Generated applications pin this package at the exact version of the generator that wrote their
`package.json`; the two publish from one git tag and cannot be installed apart.

The semantic core — the three-valued computed arithmetic, the condition evaluator, the permission
resolver — is a port of the C# runtime `Cordango.Standalone`, and the two are held together by the
decision fixtures in `tests/fixtures/` of the repository: hand-written (record, question) → answer
cases that every implementation's suite runs. Drift becomes a red test rather than a wrong answer
in production.

Part of [Cordango](https://github.com/cordango/cordango), the open application language and
compiler. Apache-2.0.

# Contributing to Cordango

Thanks for being here. This document is what you need to get a change merged.

By taking part you agree to the [Code of Conduct](CODE_OF_CONDUCT.md).

## Table of Contents

- [Getting set up](#getting-set-up)
- [Running the tests](#running-the-tests)
- [What makes a good change here](#what-makes-a-good-change-here)
- [Adding a generator target](#adding-a-generator-target)
- [Changing the language](#changing-the-language)
- [Commits and pull requests](#commits-and-pull-requests)
- [Reporting a bug](#reporting-a-bug)
- [Getting help](#getting-help)

## Getting set up

You need the **.NET 10 SDK**. Nothing else — no database, no containers.

```sh
git clone https://github.com/cordango/cordango.git
cd cordango
dotnet build Cordango.slnx
dotnet test Cordango.slnx
```

## Running the tests

```sh
dotnet test Cordango.slnx                 # everything
CORDANGO_SKIP_SDK_TESTS=1 dotnet test Cordango.slnx   # skip the slow half
```

The slow half generates eight applications from the conformance corpus, compiles each with the real
SDK, and asks EF Core to certify its own model snapshot. It reaches the package feed, so it needs a
network. It is also where most real bugs surface, so please run it before opening a pull request.

## What makes a good change here

This project generates code that other people then own and deploy, which shifts the usual bar in two
ways.

**A bug is amplified.** A mistake in an emitter is not one wrong line, it is one wrong line in every
application anybody generates from that version. Tests that compile and run the output are worth
more than tests that inspect the emitter's strings.

**Silence is the enemy.** If the generator cannot do something the definition asks for, it must say
so — with a diagnostic code and the path in the definition that caused it — and refuse the build.
`--allow-incomplete` is how a person says they know. Never emit something plausible for a construct
you could not fully translate: an expression that half-renders is a figure that is quietly wrong,
and nobody can see it in the output.

Beyond that:

- **Determinism is a contract.** The same definition, generator and scaffold produce the same bytes.
  No timestamps, no random ids, no machine paths, no locale-dependent formatting in output.
  `Cordango.Determinism.Tests` enforces it.
- **Say why in a comment, not what.** The code says what it does. A comment earns its place by
  recording the decision, the alternative that was rejected, or the bug that produced the line.
- **Warnings are errors.** That is set in `Directory.Build.props` and CI enforces it.

## Adding a generator target

A target is one whole stack — a backend and a frontend that ship together — and it plugs in by
implementing `IAppSourceGenerator` in `src/Cordango.SourceGen`. Four members: `Id`, `Version`,
`Capabilities`, `Generate`.

Two things are worth knowing before you start:

**Declare capabilities from the language, not from your emitters.** `GeneratorCapabilities` is a
claim about what the target can build, and `cordango check --target <id>` answers from it without
generating anything. A value your target will never support is *withheld with a reason sentence* —
`CapabilityCoverageTests` fails if the schema allows something you have not classified either way.

**Register it in the CLI, not the SDK.** `src/Cordango.Cli/Generate/Targets.cs` is the composition
root and the only place that knows any particular generator exists.

You do not have to write it in .NET. The extension point is a process: it describes itself on stdout
and takes a request on stdin.

## Changing the language

The App Definition schema in `schemas/` is the contract between everything else. A change there
reaches the compiler, every target, and every application anybody has already written.

- Edit the composed sources in `schemas/src/`, not the built schema.
- Add a fixture to `tests/corpus/` that exercises the new construct.
- Expect `CapabilityCoverageTests` to fail until every target has classified the new value. That is
  the test doing its job: it is asking each target to decide, out loud, whether it builds the thing.

The hand-written fixtures in `tests/fixtures/` — permissions, conditions, computed — are the
specification shared with the hosted product. **Write them by hand from the rule, never record them
from an implementation.** Recording makes one implementation the definition of correct and enshrines
its bugs as the contract the other has to match.

## Commits and pull requests

- Branch from `main`.
- One concern per pull request. A refactor and a fix in the same diff are two reviews pretending to
  be one.
- Say what changed and why in the description. If it fixes an issue, link it.
- Make sure `dotnet test Cordango.slnx` passes. CI runs the same thing on Linux, Windows and macOS.
- Generated-output changes: if your change alters what the generator emits, say so, and say whether
  it is intentional. Regenerating the corpus and diffing before and after is the quickest way to
  find out.

## Reporting a bug

Use the [issue templates](https://github.com/cordango/cordango/issues/new/choose).

For a generator bug, the single most useful thing you can attach is **the definition that produced
it** — or the smallest one that still does. `cordango.build.json` from the generated output tells us
which generator and scaffold version wrote it.

Security vulnerabilities go to [SECURITY.md](SECURITY.md), not to the issue tracker.

## Getting help

- [Issues](https://github.com/cordango/cordango/issues) — bugs and feature requests
- [Discussions](https://github.com/cordango/cordango/discussions) — questions and ideas
- [hello@cordango.com](mailto:hello@cordango.com)

## License

Contributions are licensed under [Apache-2.0](LICENSE), the same as the project.

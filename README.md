# Cordango

[![CI](https://github.com/cordango/cordango/actions/workflows/ci.yml/badge.svg)](https://github.com/cordango/cordango/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Cordango.Cli?logo=nuget&label=Cordango.Cli)](https://www.nuget.org/packages/Cordango.Cli)
[![Release](https://img.shields.io/github/v/release/cordango/cordango?logo=github&label=release)](https://github.com/cordango/cordango/releases/latest)
[![Docs](https://img.shields.io/badge/docs-docs.cordango.com-0f766e)](https://docs.cordango.com)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue)](LICENSE)

Cordango is an open application language and deterministic compiler for building complete business
applications. Describe what the application should do, generate conventional source code, own it, and
deploy it anywhere.

The same definition either runs on **Cordango Platform** — the hosted product, currently in
invite-only beta at [cordango.com](https://cordango.com) — or compiles to a **standalone
application** that is yours.

One definition, many targets. `dotnet-vue` is the first first-party target and the one that works
today; Node, Python and React are on the way. See [Targets](#targets).

What comes out has no runtime dependency on Cordango. No licence server, no account, no model API,
no phone home. It is an ordinary project that keeps working whether or not this
project does.

> **Status: pre-alpha.** The standalone generator works end to end and nothing here is stable yet.

📖 **[Documentation](https://docs.cordango.com)** · [Quickstart](https://docs.cordango.com/quickstart) · [CLI reference](https://docs.cordango.com/cli/install) · [Concepts](https://docs.cordango.com/concepts) · [Building with an agent](https://docs.cordango.com/ai/overview)

## Table of Contents

- [Documentation](#documentation)
- [Targets](#targets)
- [Requirements](#requirements)
- [Quick Start](#quick-start)
  - [Install](#install)
  - [Generate an application](#generate-an-application)
- [What you get](#what-you-get)
- [Commands](#commands)
- [Nothing is dropped silently](#nothing-is-dropped-silently)
- [Determinism](#determinism)
- [Examples](#examples)
- [Packages](#packages)
- [Building from source](#building-from-source)
- [Contributing](#contributing)
- [Security](#security)
- [Getting help](#getting-help)
- [License](#license)

## Documentation

Full documentation is at **[docs.cordango.com](https://docs.cordango.com)**.

| | |
| --- | --- |
| [Quickstart](https://docs.cordango.com/quickstart) | From nothing to a running application |
| [CLI](https://docs.cordango.com/cli/install) | Installing, and every command |
| [Concepts](https://docs.cordango.com/concepts) | Semantic source, the App Definition, the schema, targets |
| [Authoring](https://docs.cordango.com/guides/authoring) | Editing an app, semantic operations, roles and access |
| [Deploying](https://docs.cordango.com/guides/deploying) | Getting a generated application into production |
| [Validation and CI](https://docs.cordango.com/guides/validation) | Checking a definition in a pipeline |
| [Building with an agent](https://docs.cordango.com/ai/overview) | Claude Code, Codex, Cursor |
| [API reference](https://docs.cordango.com/api-reference/introduction) · [MCP](https://docs.cordango.com/mcp/overview) | Talking to an instance |

## Targets

A **target** is one whole stack — a backend and a frontend that ship together. `--target` names it,
and `cordango targets` prints what each one can and cannot build.

| Target | Backend | Frontend | Status |
| --- | --- | --- | --- |
| `dotnet-vue` | ASP.NET Core, EF Core, PostgreSQL | Vue 3, Vuetify | **Available** |
| `node-vue` | Node, TypeScript | Vue 3, Vuetify | Designed, next up |
| Python | Python | — | Planned |
| React | — | React | Planned |

**Only `dotnet-vue` exists today.** The others are not implemented and `--target` will not accept
them yet — they are listed so you can see where this is going, not so you can use them.

Nothing about the language is tied to .NET. The schema, the compiler, the validator and the
capability model are all target-agnostic; `dotnet-vue` is simply the one that was written first. A
generator does not have to be written in .NET either — the extension point is a process that
describes itself on stdout and takes a request on stdin, so one written in Go or Python is as welcome
as one written in C#.

[Targets](https://docs.cordango.com/concepts/targets) has the detail.

## Requirements

**To run `cordango`:** nothing at all. The binary is self-contained and carries its own runtime —
no .NET SDK, no .NET runtime, no ICU.

**To run what it generates:** Docker, for now. `docker compose up --build` brings the application and
its database up together, which is why the quickstart is one command.

You do not have to use it. A generated application is an ordinary project in whatever language the
target emits, so you can run it directly with that toolchain — `dotnet run` and `npm run dev` for
`dotnet-vue`, and whatever is native to the target otherwise. You bring your own PostgreSQL in that
case. Running a generated application with no Docker and nothing to set up is coming.

## Quick Start

### Install

**macOS and Linux**

```sh
brew install cordango/tap/cordango
```

Or without Homebrew:

```sh
curl -fsSL https://cordango.com/install.sh | sh
```

**Windows**

```powershell
scoop bucket add cordango https://github.com/cordango/scoop-bucket
scoop install cordango
```

**If you already have the .NET SDK**

```sh
dotnet tool install -g Cordango.Cli
```

Same command, same version, published from the same tag. A convenience rather than the main route:
Cordango is an application language, and generating a Go or React target should not require a .NET
SDK to run the compiler.

### Generate an application

```sh
cordango new expenses            # a workspace, with one application in it
cd expenses
cordango check                   # parse, lower and validate. No model, no database.
cordango targets                 # what can be generated, and what each target supports

cordango build --target standalone --out ../expenses-app
cd ../expenses-app
docker compose up --build
```

That last command is the whole deployment — no `.env` to write first, no migration step, no password
to look up. Open <http://localhost:8080> and the first screen asks you to create the administrator
account.

The longer version, with an application worth reading at the end of it, is the
**[Quickstart](https://docs.cordango.com/quickstart)**.

## What you get

An ordinary repository you own. From `dotnet-vue`:

```
api/        ASP.NET Core, MVC controllers, EF Core, migrations you can read
web/        Vue 3 and Vuetify, one component per screen
runtime/    the Cordango runtime as source, or a package reference — your choice
Dockerfile
docker-compose.yml
```

Another target lays it out in whatever is idiomatic for its own stack. What every target owes you is
the same list, because it comes from the definition rather than from the language: entities and their
schema, a REST API, roles and per-field permissions enforced on the server, commands with their
guards and effects, workflows, computed fields and rollups, sign-in, a first-run setup screen, and a
demo dataset.

Delete the toolchain afterwards and it still builds.

## Commands

| Command | What it does |
| --- | --- |
| `cordango new <app>` | Create a workspace and its first application |
| `cordango add app <name>` | Add another application to the workspace |
| `cordango import <definition.json>` | Bring an existing App Definition in as editable source |
| `cordango check [--target <id>]` | Parse, lower and validate. With `--target`, ask whether that generator can build it |
| `cordango targets` | What this build can generate, and what each target deliberately will not |
| `cordango build --target <id> --out <dir>` | Generate a whole application |
| `cordango inspect [path]` | Describe the workspace, one application, or one aggregate |
| `cordango vocabulary [<name>]` | What may be written |
| `cordango fmt` | Rewrite every source file in canonical form |
| `cordango doctor` | Check the workspace for problems that are not source errors |

`cordango --help` lists the rest, including `login`, `publish` and `whoami` for talking to an
instance. Every command is documented at [docs.cordango.com/cli](https://docs.cordango.com/cli/install).

## Nothing is dropped silently

**A build refuses rather than quietly shipping less than the definition asks for.** Anything the
target cannot do is reported with a diagnostic code and the path in the definition that caused it,
and the build stops.

`--allow-incomplete` is how you say you know. The application is generated, every gap is listed in
its README, and `cordango.build.json` records them permanently — so a partial build can never pass
for a complete one later.

The codes separate two different kinds of news: `CORD21xx` is *this target will never do that*
(record history needs an audit trail a standalone application does not keep), and `CORD23xx` is
*not generated yet*, which a later release removes with no change to your definition.

See [Targets](https://docs.cordango.com/concepts/targets) for what each one supports.

## Determinism

The same App Definition, generator version and scaffold version produce the same files, byte for
byte. No timestamps, no random identifiers, no machine paths, no locale-dependent formatting in
generated output. CI generates the same fixture twice and compares.

Seed data works the same way: `cordango build --seed 42` produces the same dataset every time, dates
included. If you would rather the demo data looked current, the generated application re-anchors it
on the day it loads when you set `SEED_DATE=today` — a run-time choice that gives up reproducibility
on purpose, and deliberately not a build-time one, so the build stays deterministic either way.

## Examples

Complete applications, as source you can clone, read, change and build:
**[cordango/examples](https://github.com/cordango/examples)**.

Start with `expenses` — the smallest one that is still complete. Read `budget-planner` for the
calculation plane: rollups across a window, figures read across a reference, and a cash balance that
reads the row before it.

## Packages

| Package | Who references it |
| --- | --- |
| [`Cordango.Cli`](https://www.nuget.org/packages/Cordango.Cli) | Nobody — it is the tool. Installed, not referenced. |
| [`Cordango.Standalone`](https://www.nuget.org/packages/Cordango.Standalone) | A *generated* application. Records, hooks, permissions, queries, the directory, the wire. |

Both are published from one git tag and share a version, because a generated application pins the
runtime at the version of the generator that wrote its project file.

The compiler, the generator SDK and the `dotnet-vue` generator are **not** published: a .NET tool
package contains its whole publish output, so all three ship inside `Cordango.Cli` as files, and it
declares no dependencies at all.

Writing a generator for another target does not need them either. The extension point is a process —
it describes itself on stdout and takes a request on stdin — so a generator written in Go or Python
is as welcome as one written in .NET.

## Building from source

Requires the .NET 10 SDK. No database, no containers.

```sh
dotnet build Cordango.slnx
dotnet test Cordango.slnx
```

Some tests generate an application and run the real SDK over it — compiling it, and asking EF to
certify its model snapshot against its own model — which reaches the package feed.
`CORDANGO_SKIP_SDK_TESTS=1` skips those; the rest runs offline.

## Contributing

Pull requests are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md) for how to get set up, what the
bar is for a change here, and how to add a generator target.

By taking part you agree to the [Code of Conduct](CODE_OF_CONDUCT.md).

## Security

Please report vulnerabilities privately — see [SECURITY.md](SECURITY.md). A flaw in a generated
application's authentication or permission enforcement is a flaw in the generator, and it is the most
serious kind of report we can get.

## Getting help

- [Documentation](https://docs.cordango.com) — start here
- [Issues](https://github.com/cordango/cordango/issues) — bugs and feature requests
- [Discussions](https://github.com/cordango/cordango/discussions) — questions and ideas
- [hello@cordango.com](mailto:hello@cordango.com)

## License

Apache-2.0. See [LICENSE](LICENSE).

**Source generated by this toolchain belongs to whoever generated it** and is not covered by this
licence. Generate it, modify it, ship it.

CORDANGO is a trademark. The licence covers the software, not the name.

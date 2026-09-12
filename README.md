# Cordango

[![CI](https://github.com/cordango/cordango/actions/workflows/ci.yml/badge.svg)](https://github.com/cordango/cordango/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Cordango.Cli?logo=nuget&label=Cordango.Cli)](https://www.nuget.org/packages/Cordango.Cli)
[![Release](https://img.shields.io/github/v/release/cordango/cordango?logo=github&label=release)](https://github.com/cordango/cordango/releases/latest)
[![Docs](https://img.shields.io/badge/docs-docs.cordango.com-0f766e)](https://docs.cordango.com)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue)](LICENSE)

**The open foundation of the Cordango application platform.** Define a complete business application
in a portable format, run it on Cordango, or generate conventional source code you own and deploy
anywhere.

📖 **[Documentation](https://docs.cordango.com)** ·
[Quickstart](https://docs.cordango.com/quickstart) ·
[CLI](https://docs.cordango.com/cli/install) ·
[Concepts](https://docs.cordango.com/concepts) ·
[Building with an agent](https://docs.cordango.com/ai/overview)

## What this is

Cordango is a platform companies run their internal applications on, currently in invite-only beta
at [cordango.com](https://cordango.com). This repository is what sits underneath it: the app format,
the compiler, the validator and the standalone generator, all Apache-2.0.

An application here is described rather than coded. Entities and their fields, who may read and
write what, the states a record moves through, the screens people work in, the figures worked out
when a row is saved. That description is an **App Definition**, written as YAML, and it is the whole
application. There is no second source of truth hiding in a codebase somewhere, and no scaffold that
drifts from the model the day after it is generated.

From there it goes one of two ways.

Publish it to the platform and it runs, sharing People, Organizations and a Calendar with every
other app in the company, with audit history and governance already in place.

Or build it, and get a conventional repository: an API, a front end, a database schema, a Dockerfile,
in whatever stack the target emits. Same file, same application, two destinations.

The generated half has no runtime dependency on us. No licence server, no account, no model API, no
phone home. Delete the toolchain afterwards and it still builds. That is most of the reason this
repository is open: what you generate has to keep working whether or not we do.


## From definition to application

```yaml
entities:
  Expense:
    fields:
      amount: money
      category: string
      submittedAt: datetime

roles:
  employee:
    can:
      - create: Expense
      - read: Expense
```

`cordango build` turns that into a repository with an API, a front end and a Dockerfile. Entities
and their schema, REST and MCP, sign-in, roles and per-field permissions enforced on the server,
commands with their guards and effects, workflows, computed fields and rollups, a first-run setup
screen, and a demo dataset.

## Quick start

**macOS and Linux**

```sh
brew install cordango/tap/cordango
# or
curl -fsSL https://cordango.com/install.sh | sh
```

**Windows**

```powershell
scoop bucket add cordango https://github.com/cordango/scoop-bucket
scoop install cordango
```

**With the .NET SDK already installed**

```sh
dotnet tool install -g Cordango.Cli
```

Then:

```sh
cordango new expenses            # a workspace, with one application in it
cd expenses
cordango check                   # parse, lower and validate. No model, no database.
cordango configure               # where these apps run. Asked once, committed.
cordango build

cd generated/expenses
docker compose up --build
```

That last command is the whole deployment. No `.env` to write, no migration step, no password to
look up. Open <http://localhost:8080> and the first screen asks you to create the administrator
account.

Generated applications go to `generated/<app>/` and nowhere else. There is no `--out`. The directory
is gitignored because everything in it comes from the source beside it, and you can move it out
whenever it is ready to have a life of its own.

## Targets

A target is one whole stack, a backend and a frontend that ship together.

| Target | Backend | Frontend | Status |
| --- | --- | --- | --- |
| `dotnet-vue` | ASP.NET Core, EF Core | Vue 3, Vuetify | Available |
| `node-vue` | Node, TypeScript, Express | Vue 3, Vuetify | Available. Workflows and rollups not generated yet |
| Python | Python | | Planned |
| React | | React | Planned |

**The front end is one tree, not two copies.** The Vue shell is a REST client: it talks to whatever
answers the HTTP contract and never asks what the backend is written in. One emitter produces it,
both targets call that emitter, and a test asserts the two outputs are byte-identical across every
example application. Adding a third target means writing a backend, not a second front end.

**PostgreSQL is the only database today.** Which one a generated application uses is the target's
choice, never something your definition has to say. `node-vue` runs on [PGlite](https://pglite.dev)
when no `DATABASE_URL` is set, so `npm start` is a complete application with nothing to install.

Nothing about the format is tied to .NET. A generator is a process that describes itself on stdout
and takes a request on stdin, so one written in Go or Python is as welcome as one written in C#.
[Targets](https://docs.cordango.com/concepts/targets) has the detail.

## Custom code

Some things a definition cannot say: rounding, a checksum, a rule that spans two fields. Put C# in
`custom/dotnet/` and call it by name.

```csharp
[CordangoFunctions]
public static class Rounding
{
    [CordangoFunction("round", Description = "Nearest whole number, halves away from zero.")]
    public static decimal? Round(decimal? value) =>
        value is null ? null : decimal.Round(value.Value, 0, MidpointRounding.AwayFromZero);
}
```

```yaml
computed:
  expr: custom.round(active_exact)
```

Hooks work the same way. Mark a method `[BeforeCreate]` or `[AfterUpdate]` and it runs on every
write with the record and a context in hand. A before-hook can change the record or throw to refuse
the write; an after-hook runs once the write is already decided.

`cordango custom` creates the folder and a project file your editor understands. The code is
compiled into the generated application, so a wrong signature is a build error rather than something
you find out about later.

`dotnet-vue` only for now. An application carrying custom code cannot run on the platform, which
interprets definitions and has no compiler.

## Commands

| Command | What it does |
| --- | --- |
| `cordango new <app>` | Create a workspace and its first application |
| `cordango add app <name>` | Add another application to the workspace |
| `cordango configure` | Decide once where this workspace's apps run, and commit it |
| `cordango import <file>` | Bring an App Definition in as editable source |
| `cordango check [--target <id>]` | Parse, lower and validate. With `--target`, ask whether that generator can build it |
| `cordango targets` | What this build can generate, and what each target deliberately won't |
| `cordango build [--target <id>]` | Do what `configure` said, or override it once |
| `cordango custom` | Set up this app's own code and the project an editor reads |
| `cordango inspect [path]` | Describe the workspace, one application, or one aggregate |
| `cordango fmt` | Rewrite every source file in canonical form |
| `cordango doctor` | Check the workspace for problems that aren't source errors |

`cordango --help` lists the rest, including `login`, `publish` and `whoami`.

## Two guarantees

**Nothing is dropped silently.** A build refuses rather than shipping less than the definition asks
for. Anything the target can't do is reported with a diagnostic code and the path that caused it.
`--allow-incomplete` is how you say you know: the gaps are listed in the generated README and
recorded in `cordango.build.json`, so a partial build can never pass for a complete one later.
`CORD21xx` means this target will never do that. `CORD23xx` means not generated yet, which a later
release removes with no change to your definition.

**Output is deterministic.** The same definition and generator version produce the same files, byte
for byte. No timestamps, no random identifiers, no machine paths. CI generates the same fixture
twice and compares. `cordango build --seed 42` produces the same demo dataset every time, dates
included.

## Requirements

To run `cordango`, nothing. The binary is self-contained and carries its own runtime.

To run what it generates, Docker is the easy path. You can also use the toolchain directly:
`dotnet run` and `npm run dev` for `dotnet-vue`, `npm install && npm run build && npm start` for
`node-vue`. `dotnet-vue` needs a PostgreSQL; `node-vue` brings its own.

## Examples

Complete applications you can clone, read and build:
**[cordango/examples](https://github.com/cordango/examples)**.

Start with `expenses`, the smallest one that is still complete. Read `budget-planner` for the
calculation plane: rollups across a window, figures read across a reference, and a cash balance that
reads the row before it.

## Building from source

Requires the .NET 10 SDK. No database, no containers.

```sh
dotnet build Cordango.slnx
dotnet test Cordango.slnx
```

Some tests generate an application and run the real SDK over it, which reaches the package feed.
`CORDANGO_SKIP_SDK_TESTS=1` skips those. The rest runs offline.

## Contributing

Pull requests are welcome. [CONTRIBUTING.md](CONTRIBUTING.md) covers getting set up, the bar for a
change here, and how to add a target. By taking part you agree to the
[Code of Conduct](CODE_OF_CONDUCT.md).

## Security

Report vulnerabilities privately, see [SECURITY.md](SECURITY.md). A flaw in a generated
application's authentication or permission enforcement is a flaw in the generator, and it is the
most serious kind of report we can get.

## Getting help

- [Documentation](https://docs.cordango.com), start here
- [Issues](https://github.com/cordango/cordango/issues) for bugs and feature requests
- [Discussions](https://github.com/cordango/cordango/discussions) for questions and ideas
- [hello@cordango.com](mailto:hello@cordango.com)

## License

Apache-2.0. See [LICENSE](LICENSE).

**Source generated by this toolchain belongs to whoever generated it** and isn't covered by this
licence. Generate it, modify it, ship it.

CORDANGO is a trademark. The licence covers the software, not the name.

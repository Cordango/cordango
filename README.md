# Cordango

Cordango is an open application language and deterministic compiler for building complete business
applications. Define what your application should do, generate conventional source code, own it, and
deploy it anywhere.

There is no runtime dependency on Cordango in what you generate. No license server, no account, no
model API. The generated project is an ordinary ASP.NET Core and Vue application that keeps working
whether or not this project does.

> Status: pre-alpha. The standalone generator is under construction and nothing here is stable yet.

## What is in this repository

| Path | What it is |
| --- | --- |
| `src/Cordango.Compiler` | The App Definition contract: schema gate, semantic validation, normalizer, the Cord semantic model, the compiler that turns a definition into a runtime manifest, and the access-token format the CLI uses to talk to an instance. |
| `src/Cordango.Cli` | The `cordango` command. |
| `src/Cordango.SourceGen` | The generator SDK: capabilities, target validation, the file runner, the external generator protocol. |
| `src/Cordango.SourceGen.DotNetVue` | The first-party generator. ASP.NET Core, EF Core, PostgreSQL, Vue 3, Vuetify. |
| `src/Cordango.Standalone` | The runtime a generated application runs on — records, hooks, permissions, the built-in directory, the error wire — plus the scaffold it starts from. Emitted as source into what you generate, not referenced as a package. |
| `schemas/` | The App Definition JSON Schema and its composed sources. |
| `docs/` | Design notes, including what the standalone scaffold takes from prior art and why. |
| `tests/corpus/` | Application definitions used as the conformance corpus. |
| `tests/` | Compiler, CLI, generator, standalone, determinism and compatibility suites. |

## The two products

Cordango Open Source builds **one application**. It has its own backend, frontend, database, users,
authentication, permissions, Organizations and People. It is not multi-tenant, it contains no AI at
runtime, and it belongs to whoever generated it.

Cordango Platform connects, operates and governs **many applications** as one company system: shared
people and organizations across apps, cross-app references, audit and record history, governance, AI,
search, marketplace and managed hosting. That is a separate, commercial product.

The same application definition works in both places. What differs is what each target supports, and
every target says so out loud rather than silently dropping what it cannot do.

## How it fits together

```
Human or AI
     |
Cordango source
     |
  compiler
     |
App Definition          the canonical contract
     |
     +----------------------+
     |                      |
Cordango Platform     Standalone generator
                            |
                     complete source project
                            |
                      deploy anywhere
```

## Installing

**macOS and Linux**

```
curl -fsSL https://cordango.com/install.sh | sh
```

**macOS and Linux, with Homebrew**

```
brew install cordango/tap/cordango
```

**Windows**

```
scoop bucket add cordango https://github.com/cordango/scoop-bucket
scoop install cordango
```

One self-contained binary, about 39 MB, carrying its own runtime. **It does not need .NET
installed** — not the SDK, not the runtime, not ICU. Uninstalling is deleting the file.

**If you already have the .NET SDK**, there is a shorter way in:

```
dotnet tool install -g Cordango.Cli
```

Same command, same version, published from the same tag. It is a convenience rather than the main
route: Cordango is an application language, and generating a Go or React target should not require a
.NET SDK just to run the compiler.

### The packages

There are two, and both have somebody who actually references them.

| Package | Who references it |
| --- | --- |
| `Cordango.Cli` | Nobody — it is the tool. Installed, not referenced. |
| `Cordango.Standalone` | A GENERATED application. Records, hooks, permissions, queries, the directory, the wire. |

Both are published from one git tag and share a version, because a generated application pins the
runtime at the version of the generator that wrote its project file.

The compiler, the generator SDK and the `dotnet-vue` generator are **not** published. A .NET tool
package contains its whole publish output, so all three ship inside `Cordango.Cli` as files and it
declares no dependencies — `cordango` installs with nothing else to restore.

Writing a generator for another target does not need them either. The extension point is a process:
it describes itself on stdout and takes a request on stdin, so a generator written in Go or Python
is as welcome as one written in .NET. `Cordango.SourceGen` will be published when a second target
exists and has something to say about the shape of that contract — freezing an interface on an
immutable feed with one implementation to learn from is how you get an interface that fits one
implementation and fights the next.

## Generating an application

```
cordango new expenses            # a workspace, with one application in it
cd expenses
cordango check                   # parse, lower and validate. No model, no database.
cordango targets                 # what can be generated, and what each target supports

cordango build --target standalone --out ../expenses-app
cd ../expenses-app
docker compose up --build
```

That last command is the whole deployment: no `.env` to write first, no migration step, no password
to look up. Open <http://localhost:8080> and the first screen asks you to create the administrator
account.

What comes out is an ordinary repository — `api/` (ASP.NET Core, EF Core, migrations you can read),
`web/` (Vue 3 and Vuetify, one component per screen), a Dockerfile and a compose file. Delete the
toolchain afterwards and it still builds.

**A build refuses rather than quietly shipping less than the definition asks for.** Anything this
target cannot do is reported with a code and the path in the definition that caused it, and the build
stops. `--allow-incomplete` says you know: the application is generated, the gaps are listed in the
generated README, and `cordango.build.json` carries them permanently so a partial build can never
pass for a complete one later.

## Determinism

The generator is deterministic. The same App Definition, generator version, scaffold version and
controls version produce the same files, byte for byte. No timestamps, no random identifiers, no
machine paths, no locale-dependent formatting in generated output. CI generates the same fixture
twice and compares.

Seed data works the same way. `cordango build --seed 42` produces the same dataset every time,
including dates. If you want data anchored to today instead, `--seed-date today` says so explicitly
and gives up reproducibility on purpose.

## Building

Requires the .NET 10 SDK.

```
dotnet build Cordango.slnx
dotnet test Cordango.slnx
```

No database and no containers. Some of the standalone tests do generate an application and run the
real .NET SDK over it — compiling it, and asking EF to certify the model snapshot against its own
model — which reaches the package feed. `CORDANGO_SKIP_SDK_TESTS=1` skips those; the rest of the
suite runs offline.

## License

Apache-2.0. See [LICENSE](LICENSE).

Source generated by this toolchain belongs to the person or organization that generated it and is not
covered by this license. Generate it, modify it, ship it.

CORDANGO is a trademark. The license covers the software, not the name.

# Cordango

[![CI](https://github.com/cordango/cordango/actions/workflows/ci.yml/badge.svg)](https://github.com/cordango/cordango/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Cordango.Cli?logo=nuget&label=Cordango.Cli)](https://www.nuget.org/packages/Cordango.Cli)
[![Release](https://img.shields.io/github/v/release/cordango/cordango?logo=github&label=release)](https://github.com/cordango/cordango/releases/latest)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue)](LICENSE)

**Build full-stack business apps from YAML. Ask your agent to write the definition. Generate the full app.**

AI can write an impressive amount of code. It can also spend half an afternoon getting authentication, forms, migrations and permissions to agree with each other.

A lot of that work is the same in every application. So why generate it again and again?

Cordango lets you define the application instead. Your entities, relationships, screens, calculations, permissions, commands and workflows go into an App Definition. The compiler takes it from there.

You get a working backend, frontend and database setup, a full application ready to be used with no other dependencies. Currently supporting .NET+Vue and Postgres... Node being worked on and more to come

[Quickstart](https://docs.cordango.com/quickstart) | [Documentation](https://docs.cordango.com) | [Examples](https://github.com/cordango/examples) | [Using an AI agent](https://docs.cordango.com/ai/overview)

## What does it actually generate?

Take an expense app. Someone submits a claim, a manager approves or rejects it, the employee gets notified, and finance marks it reimbursed.

Normally that's a database schema, API, authentication, role checks, forms, dashboards, business actions and probably a bunch of glue code.

In Cordango, those things are described together. Here's a small taste of the YAML:

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

That's just a fragment, of course. A complete app definition can also describe the approval process, what happens when someone clicks Approve, what an employee is allowed to see, and what appears on the dashboard.

Then:

```sh
cordango check
cordango build
```

Cordango produces the app, including its API and UI, without an AI model generating thousands of lines of implementation code.

Want to see something beyond CRUD? [Look at the examples](https://github.com/cordango/examples). The warehouse example covers stock movements, order picking, deliveries, computed availability, reorder rules and more.

<!-- Add a short real demo GIF here. Show a definition, the build, and the running app. -->

## Try it

### Install

The CLI is a standalone binary. You don't need .NET, Docker or a database to use it.

**macOS with Homebrew**

```sh
brew install cordango/tap/cordango
```

**Windows with Scoop**

```powershell
scoop bucket add cordango https://github.com/cordango/scoop-bucket
scoop install cordango
```

**Linux and macOS with the install script**

```sh
curl -fsSL https://cordango.com/install.sh | sh
```

**Prefer installing the binary yourself?** Grab the right build for your system from [GitHub Releases](https://github.com/cordango/cordango/releases/latest).

You can also install it as a .NET global tool if you already use the SDK, but that's entirely optional:

```sh
dotnet tool install -g Cordango.Cli
```

### Create and run an app

```sh
cordango new expenses
cd expenses
cordango check
cordango configure
cordango build

cd generated/expenses
docker compose up --build
```

Open `http://localhost:8080`. The generated app asks you to create its first administrator account.

That's it. You now have an independently running application.

Generated code lives under `generated/<app>/`. Move it out of the workspace whenever you want to maintain it separately. It doesn't need a Cordango login, licence server or model API.

## Use it with Claude Code or another agent

This is where it gets interesting.

Instead of asking Claude to write a complete .NET and Vue project, ask it to create a Cordango app definition. Let the compiler handle the repetitive implementation.

For example:

> Build an expense management app with employee submissions, manager approval, reimbursement tracking and a dashboard. Use Cordango, validate the definition, and build the standalone app.

`cordango new` creates `CLAUDE.md` and `AGENTS.md` to give coding agents context. The CLI also exposes the vocabulary and validation tools they need.

```sh
cordango vocabulary
cordango check
cordango build
```

The agent still has to get the business rules right. The difference is that it works with a defined language and a compiler instead of inventing a new application architecture each time.

See [Building with an agent](https://docs.cordango.com/ai/overview) for the workflow.

## It's not just CRUD

The app definition can describe more than fields and tables:

- **Data:** entities, references, calculated values and rollups
- **Behavior:** commands, guards, state transitions and workflows
- **UI:** forms, tables, dashboards, boards and other supported views
- **Access:** roles, entity permissions and field-level rules
- **Integration:** REST, MCP and application actions

Take the warehouse example. Available stock isn't an editable number someone can accidentally overwrite. It's calculated from stock movements and reservations. Shipping records a movement, availability changes, and the replenishment rules use that result.

The definition describes those relationships. The generated application implements them within the capabilities of its chosen target.

Of course, YAML isn't magic. If a rule can't be expressed in the language, it needs custom code, another approach, or an addition to the compiler. We don't want to pretend otherwise.

## Available targets

| Target | Backend | Frontend | Status |
| --- | --- | --- | --- |
| `dotnet-vue` | ASP.NET Core, EF Core, PostgreSQL | Vue 3, Vuetify | Available |
| `node-vue` | Node.js, TypeScript, Express | Vue 3, Vuetify | Available, but workflows and rollups aren't generated yet |
| Python | Planned | | Not available |
| React | | Planned | Not available |

The Node target can use PGlite without a separate database server. The .NET target uses PostgreSQL.

A target either supports a feature or tells you that it doesn't. It shouldn't quietly leave half your application out.

```sh
cordango targets
cordango check --target dotnet-vue
cordango build --target dotnet-vue
```

## What about custom code?

Not everything belongs in an app definition.

Sometimes you need an unusual calculation, an integration or some specific domain logic. The .NET generator supports custom C# functions and hooks.

```csharp
[CordangoFunctions]
public static class Rounding
{
    [CordangoFunction("round", Description = "Round to a whole number.")]
    public static decimal? Round(decimal? value) =>
        value is null ? null : decimal.Round(value.Value, 0, MidpointRounding.AwayFromZero);
}
```

Then reference it in the definition:

```yaml
computed:
  expr: custom.round(active_exact)
```

Run `cordango custom` to set up the custom code project.

Custom C# currently works with `dotnet-vue`. It is not supported by the hosted platform runtime, and it doesn't automatically translate to Node.

## What makes the compiler different from a scaffold?

A scaffold is useful on day one. The trouble starts after day one, when the model changes and the generated files have been edited by hand.

With Cordango, the App Definition stays the source for the parts the compiler owns. You change the definition and build again. If you need logic that the definition doesn't cover, use the supported custom-code extensions.

There are two rules we're serious about.

**Same input, same output.** With the same definition and generator version, the output is deterministic. We test this rather than relying on a claim.

**Don't hide missing features.** If a target can't generate something, the build reports it. You can explicitly use `--allow-incomplete`, but the omissions are recorded in the generated README and build metadata. A partial application shouldn't pass as a complete one.

## A few useful commands

| Command | What it's for |
| --- | --- |
| `cordango new <name>` | Create a workspace and first app |
| `cordango add app <name>` | Add an application |
| `cordango check` | Validate the definition |
| `cordango configure` | Choose where the application runs |
| `cordango build` | Generate the application |
| `cordango targets` | Show supported generator targets |
| `cordango vocabulary` | Show the language capabilities to agents |
| `cordango inspect` | Inspect an app or part of a workspace |
| `cordango fmt` | Format the source |
| `cordango doctor` | Check the development environment |
| `cordango custom` | Prepare custom C# code |

Use `cordango --help` for the full list.

## What Cordango isn't

It's not a promise that any prompt can turn into any application.

It's not a replacement for hand-written software when you need total freedom over every implementation detail.

And it's not necessary to sign up for the Cordango platform to get value from this repository.

We're trying to make the repetitive parts of business software predictable, and leave developers and AI agents to work on the parts that aren't.

If you disagree with how we're approaching that, we'd genuinely like to hear why.

## Cordango Cloud is optional

The compiler and CLI are the open-source developer tools. They can generate applications that run independently.

[Cordango Cloud](https://cordango.com) is a separate hosted platform for running business apps on a shared company foundation, with common people, organizations and other platform capabilities.

Use it if that solves a problem for you. Otherwise, just generate the code and host it yourself.

## Examples, docs and help

- [Example applications](https://github.com/cordango/examples)
- [Documentation](https://docs.cordango.com)
- [Quickstart](https://docs.cordango.com/quickstart)
- [Agent workflow](https://docs.cordango.com/ai/overview)
- [Issues](https://github.com/cordango/cordango/issues)
- [Discussions](https://github.com/cordango/cordango/discussions)

## Build from source

You'll need the .NET 10 SDK.

```sh
dotnet build Cordango.slnx
dotnet test Cordango.slnx
```

Some SDK integration tests access package feeds. Set `CORDANGO_SKIP_SDK_TESTS=1` if you need to skip them while working offline.

Contributions are welcome. Read [CONTRIBUTING.md](CONTRIBUTING.md) first. For vulnerabilities, please use the private reporting guidance in [SECURITY.md](SECURITY.md), not a public issue.

## Licence

The toolchain is Apache 2.0. See [LICENSE](LICENSE).

The source code generated by Cordango belongs to you. You can change it, redistribute it and run it without the Cordango toolchain.

The Cordango name and branding are not licensed under Apache 2.0.

---

Still early. Things will change. There are bugs, missing pieces, and decisions we haven't made yet.

If you try it, tell us where it breaks or where it feels unnecessarily complicated. That's much more useful to us than another star.

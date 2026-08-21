# What the standalone scaffold takes from TCDev.APIGenerator

Cordango's standalone target generates a conventional ASP.NET Core application. The question this
document answers, once, is how much of that application should be *generated per entity* and how
much should be *written once and shared* — and whether an existing answer to that question could be
adopted rather than rewritten.

The existing answer assessed here is [DeeJayTC/net-dynamic-api][repo] (`TCDev.APIGenerator`), read at
commit `4279e35`, February 2023, .NET 6. It solves a neighbouring problem: turn annotated POCOs into
a REST API at runtime. Verdicts below are **adopt** (take the package as a dependency), **absorb**
(lift the code), or **pattern** (take the idea, write our own).

[repo]: https://github.com/DeeJayTC/net-dynamic-api

## The verdict in one line

**Pattern, throughout. Nothing is adopted as a dependency and nothing is lifted verbatim.** The
shape of the thing — generic machinery once, thin types per entity — is exactly right and is the
architecture of `Cordango.Standalone`. The mechanism underneath it is built for a problem we do not
have, and the code carries defects a generator would replicate into every application it produced.

## Why not adopt the package

The library targets .NET 6 and its last release is `0.7.1-rc1`. It pulls in Swashbuckle,
Newtonsoft.Json, EntityFramework.Triggers and `EFCore.AutomaticMigrations` transitively, and its
optional modules add OData, GraphQL and RabbitMQ. A generated Cordango application should have a
dependency list its owner can read in one screen and audit themselves; inheriting a stack to get one
generic controller inverts that.

The deeper reason is the one below.

## The mechanism we do not need

`TCDev.APIGenerator` discovers its entities **at runtime, by reflection**: `AddAssembly` scans for
`[Api]`, `GenericTypeControllerFeatureProvider` closes `GenericController<T,TId>` over each
discovered type, `GenericControllerRouteConvention` rewrites the route from the attribute, and
`GenericDbContext.OnModelCreating` calls `ApplyConfigurationsFromAssembly` plus `builder.Entity(t)`
for whatever else it found.

That machinery exists to answer a question at startup that **we answer at generation time**. We know
every entity, every route and every key type when we emit the code. Reflection buys us nothing there
and costs three things that matter for a generated artifact:

- **Determinism.** Registration order becomes assembly-scan order. The whole build contract is that
  the same definition produces byte-identical output that behaves identically.
- **Legibility.** The user owns this code. `services.AddRecord<Expense>()` in a generated
  registration file is greppable; a controller conjured by a feature provider is not, and the first
  time somebody wants to add an endpoint of their own they have to understand the conjuring first.
- **Failure mode.** A mis-registration surfaces as a missing route at runtime rather than as a
  compile error.

So `Cordango.Standalone` keeps the generic controller and the generic store, and the generator emits
**explicit registration** for them. The `[Api]` attribute, the assembly scanner, the feature provider
and the route convention have no counterpart here.

## Piece by piece

| Piece | Verdict | Note |
|---|---|---|
| `IObjectBase<TId>` | **pattern** | Becomes `IRecord`, non-generic: Cordango record ids are `text`. One type parameter fewer everywhere. |
| `IHasTrackingFields` | **pattern** | Kept, near-verbatim in shape (`Created`/`CreatedBy`/`LastModified`/`LastModifiedBy`). Set centrally — see the defect below. |
| `IHasQueryFields` (`IIsQueryable`) | **dropped** | It declares queryable fields as a `string[]` on the entity. Ours are in the definition already, and the generator emits the filter surface from it. |
| `ISoftDelete`, `SoftDeletable` | **dropped for v1** | No Cordango definition expresses soft delete. Adding the interface without the language to reach it is scaffolding for a feature nobody can request. |
| `IHasTenantId` | **dropped, by product boundary** | Standalone builds one application for one organisation. Multi-tenancy is the platform. |
| `[Api]` attribute, `ApiMethodsToGenerate` | **dropped** | Configuration for runtime discovery. We generate the registration and the method set directly. |
| `GenericDbContext` + `ApplyConfigurationsFromAssembly` | **pattern, and the best idea here** | `IEntityTypeConfiguration<T>` per entity is exactly the seam we want: EF's own extension point, one generated file per entity, no reflection needed once we call it explicitly. |
| `GenericRepository<T,TId>` | **pattern** | The shape is right, the implementation is not (below). |
| Lifecycle hooks `IBeforeCreate<T>` … | **pattern, relocated** | The idea is essential — it is where computed recompute, validation and command-owned-field enforcement live at S7/S8. But see "hooks belong in the container". |
| `GenericController<T,TId>` | **pattern** | Route and verb shape carry over. Error handling, permission enforcement and status codes are ours. |
| Feature provider, route convention, application part | **dropped** | See above. |
| `ApplicationDataService` | **dropped** | A holder for two `DbContext`s and an `HttpContext`. Injecting the `DbContext` directly is clearer and testable with no HTTP context in sight. |
| `ExceptionMiddleware` | **pattern** | One place turning an exception into a JSON body is right. Ours emits the Cordango `{code, error}` wire rather than `ValidationProblemDetails`, and does not put `ex.Message` on it. |
| Scope-based authorization (`ApiAuthAttribute`, `ValidateScopes`) | **dropped** | Ours is definition-driven: roles, entity grants, per-field overrides, command grants. Not expressible as static scope strings on a type. |
| `EFCore.AutomaticMigrations` | **rejected, deliberately** | See below. |
| OData, GraphQL, RabbitMQ, Redis, DbFirst | **out of scope** | |

## Hooks belong in the container, not on the entity

In `TCDev.APIGenerator` a hook is an interface **the POCO itself implements**, and the sample puts
business logic in the entity class:

```csharp
public class Student : IObjectBase<int>, IBeforeCreate<Student>
{
    public Task<Student> BeforeCreate(Student newItem, IApplicationDataService<...> data) { ... }
}
```

For a hand-written application that is a reasonable convenience. For a **generated** one it is a
trap: the entity file is the file the generator overwrites on every build, so the one place the
framework invites you to put your logic is the one place that cannot survive a rebuild.

`Cordango.Standalone` therefore resolves hooks from the service container:

```csharp
public interface IBeforeCreate<T> { Task BeforeCreateAsync(T record, RecordContext context, CancellationToken ct); }
```

Generated hooks land in their own files and register themselves; a hand-written hook is another
registration next to them, and both run. The entity stays a POCO — pure data, safe to regenerate —
and a hook can be unit-tested with no entity graph and no HTTP context.

## Migrations: EF-native, not automatic

The sample application calls `app.UseAutomaticApiMigrations()`, which uses `EFCore.AutomaticMigrations`
to diff the model against the live database at every startup. The implementation hard-codes
`AutomaticMigrationDataLossAllowed = true` on its first call regardless of the `allowDataLoss`
argument it was given, and swallows the resulting exception to `Console.WriteLine`.

We do the opposite, and it is a decision rather than a preference. The generator invokes EF's own
`MigrationsScaffolder` with a pinned migration id, so the migration is a **file in the user's
repository that they can read, edit and review in a pull request**; startup runs `Database.Migrate()`.
After generation the application evolves by `dotnet ef migrations add` like any other EF project —
which is the whole promise of the standalone target: an ordinary application, not one that only our
toolchain understands.

## Defects found, and what they change

Recorded because a generator amplifies: a bug lifted from here would ship in every application
Cordango ever emits.

**The `IsAssignableFrom` inversion.** Six sites test `typeof(TEntity).IsAssignableFrom(typeof(IHasTrackingFields))`
where they mean `IsAssignableTo` — the operands are the wrong way round, so the condition is false
for every entity. The consequences are silent: **tracking fields are never populated**, and in
`GenericRepository.Delete` the soft-delete branch is never taken, so an entity that opts into
`ISoftDelete` is **hard-deleted instead**. The same inversion in `GenericDbContext.OnModelCreating`
means the tenant query filter is never applied, which in a multi-tenant deployment is a
data-exposure bug rather than a missing feature.

The lesson we take: a hand-written type-relationship test, in a place with no test covering the
negative case, is worth eliminating rather than getting right. `Cordango.Standalone` sets tracking
fields in **one** place — `SaveChanges`, over `ChangeTracker.Entries<IHasTrackingFields>()` — where
the compiler enforces the relationship and the wrong direction does not compile.

**`async void`.** `GenericRepository.Create` and `.Update` are `async void`. The controller's
`repository.Create(record, data); await repository.SaveAsync();` therefore does not await the create
— the `BeforeCreate` hook races the save, and any exception the hook throws is unobservable and
terminates the process rather than returning a 400. Our equivalents return `Task` and are awaited.

**`Update` assigns a local.** `oldRecord = newRecord;` after `Attach(oldRecord)` writes a parameter,
not the tracked entity; the line has no effect. The controller separately does
`ChangeTracker.Clear(); Update(record)`, which is what actually persists — and which also means the
`BeforeUpdate` hook's changes to the old record are discarded, and a client that omits a field
writes `null` over it.

**`IAfterCreate` calls `BeforeCreate`.** The after-create branch casts to `IBeforeCreate<TEntity>`
and invokes `BeforeCreate` a second time.

**`Find` dereferences `cacheOptions` unconditionally.** `string.Format(cacheOptions.cacheKey, id)`
runs whenever an `ICacheService` is registered, but `cacheOptions` is only non-null when the entity
carries `[Cachable]` — a `NullReferenceException` for every uncached entity in a Redis-enabled app.

**`Get(id)` compares `e.Id.ToString() == id.ToString()`.** Either it fails to translate to SQL and
loads the table, or it translates to a cast that cannot use the primary key index.

**Exception detail on the wire.** Both the controller (`return BadRequest(ex.Message)`) and
`ExceptionMiddleware` (`Detail = ex.Message`) hand raw exception text to the caller. Ours maps to a
stable `{code, error}` pair and logs the exception server-side.

**The Postgres provider does not use Postgres.** `TCDev.APIGenerator.Data.Postgres`'s
`ProviderConfig.OnConfiguring` calls `optionsBuilder.UseSqlServer(...)`, and its `AddDataContextSQL`
extension is `private`, so it is unreachable from outside the assembly. The `src/Data/**` tree looks
like a half-finished restructure of `src/TCDev.APIGenerator.*`; both copies are in the solution.

## What this leaves us

The architecture is Tim's, and it survives the assessment intact:

> generic machinery written once and compiled into a library; thin, regenerable, per-entity types;
> `IEntityTypeConfiguration<T>` as the mapping seam; lifecycle hooks as the extension point;
> EF migrations as the schema story.

What changes is that **registration is generated rather than discovered**, **hooks live in the
container rather than on the entity**, **tracking fields are set once where the type system can check
it**, and **migrations are files the user owns**. Those four are the difference between a runtime
framework and a code generator, which is the difference between the two projects.

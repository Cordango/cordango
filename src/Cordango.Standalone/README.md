# Cordango.Standalone

The runtime a [Cordango](https://github.com/cordango/cordango)-generated standalone application runs
on.

You do not usually install this yourself. `cordango build --target standalone` generates an
application that references it, and everything in it exists to serve code the generator emits
alongside: typed record entities, an `IEntityTypeConfiguration` per entity, one controller per
entity, and the definition's roles as compiled rules.

## What is in it

- **Records.** `IRecord`, `IHasTrackingFields`, and a generic store that runs lifecycle hooks and
  stamps who wrote a row and when — in one place, where the type system checks it.
- **Hooks.** Six interfaces resolved from the service container rather than implemented on the
  entity, because in a generated application the entity file is the one the generator overwrites.
- **Permissions.** An App Definition's `roles` resolved to an effective answer: union across roles,
  a specific grant beating the wildcard, field rules resolved per role before unioning, and commands
  denied unless granted.
- **Queries.** Filtering, sorting and aggregates built once from the field keys a descriptor carries,
  so an application does not carry sixteen field types times ten operators of generated comparisons.
- **A directory.** People, departments, groups, organizations and contacts — what a reference to a
  person points at when there is no platform to point at.
- **The wire.** `{code, error}` responses, global antiforgery, content-addressed file storage.

## Licence

Apache-2.0. The application generated around it is yours, unencumbered.

# Transform fixtures

These files are the contract between the implementations of integration mappings.

A provider maps a value from an external API onto a contract field: `$.properties.firstname` into
`first_name`, `$.amount_cents` divided by 100 into `amount`, HubSpot's `marketingqualifiedlead` into
the option `mql`. The compiler type-checks every mapping (`Transforms.Check`). Cordango Platform
evaluates them when it reads a provider's reply. A standalone application, once integrations are
generated, will carry emitted code that does the same. Three mechanisms, one set of answers, pinned
here as **data** so each is held to it by its own suite.

## Case shape

```json
{ "why": "...", "item": { ...one provider item... }, "from": "$.path",
  "transform": { ... } or [ ... ], "target": { "type": "select", "options": ["a", "b"] },
  "expect": <the field's value> }
```

A file's `refused` list holds mappings the compiler must reject, each with a substring of the error.

## The rules

- **Reading.** A missing property, an index past the end, or a step through `null` reads as `null`.
  Nothing throws.
- **`default`** replaces `null` and leaves everything else alone.
- **`coalesce`** replaces `null` with the first non-null value of its paths, read from the same item.
- **`enum` and `lookup`** match the value's text: a string as it is, a number in invariant form (`2`,
  `2.5`), `true`/`false`. A value not listed becomes `otherwise`, or `null` without it. A `null` stays
  `null` — `otherwise` is for a value nobody listed, not for a missing one; use `default` for that.
- **`concat`** joins its parts with `separator` (default none). A part starting with `$` is read from
  the item; anything else is literal. A part that reads `null` is skipped, separator and all.
- **`split`** splits text on the separator, trims each part and drops empty ones. With `index` it
  keeps that part, or `null` past the end; without, the result is a list.
- **`date`** reads `iso` (ISO-8601, any offset), `date` (`YYYY-MM-DD`), `epochMillis` or
  `epochSeconds` (a number or numeric text). A datetime field receives UTC as
  `yyyy-MM-ddTHH:mm:ss.fffZ`; a date field receives the UTC date as `yyyy-MM-dd`. Unreadable is `null`.
- **`multiply` and `divide`** take a number or numeric text (invariant, `.` as the decimal point) and
  give a number. Anything else is `null`.
- **`template`** replaces each `{{$.path}}` with the value's text, and a `null` with nothing.
- **Landing in the field.** A number field takes a number or numeric text; a boolean takes
  `true`/`false` or their text; a select takes a value that is one of its options and `null`
  otherwise; text fields take the value's text.

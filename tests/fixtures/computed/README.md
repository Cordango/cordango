# Computed expression fixtures

These files are the contract between the implementations of one small arithmetic.

A `computed.expr` in the definition — `total_revenue - total_costs`, `done_tasks * 100 / total_tasks`,
`min(usage, plan.cap)` — has to give the same answer everywhere it is worked out. Cordango Platform
evaluates it with `ComputedExpr.EvaluateValue`. A standalone application does not evaluate it at all:
`ComputedEmitter` turns it into a C# method at build time and the generated code computes it. Same
definition, same figures expected, two entirely different mechanisms.

That is a sharper split than the [condition fixtures](../conditions/) face, and the consequence is
worse. `ComputedEmitter` says so in its own header:

> the platform and a generated application computing different totals from one definition is the
> worst failure this project has.

A guard that answers wrongly sends one approval the wrong way and somebody notices. A total that is
quietly different in two places is found a quarter later by somebody reconciling two reports, and by
then every figure derived from it is wrong too.

So the rules are pinned here as **data**, and each implementation is asserted against the same data by
its own suite.

## The rule underneath all of them

**A blank field is zero. A figure that could not be worked out is unknown. They are not the same.**

A record with no tax has a tax of nothing, and a total that refused to add it up would be blank on
every half-filled row — so a blank NUMBER reads as zero and a blank BOOLEAN reads as false. But a
division by zero, a bound with an unknown side, a power that overflows, a duration missing an
endpoint: none of those is zero and none is an error. They are unknown, and a cap computed from an
unknown bound must not silently become no cap at all.

The distinction is easy to state and easy to lose. `min(a, b)` where both fields are blank is
**zero**, because both read as zero. `min(a, 1 / 0)` is **unknown**, because one side could not be
worked out. Most of what is pinned below is that one sentence, applied.

## What these pin, and why each one is here

1. **`01` — a blank is zero, and unknown is not.** The two kinds of missing, side by side, and what
   each does to the arithmetic around it. A blank is absorbed (`blank + 1` is `1`); an unknown
   spreads (`(1 / 0) + 1` is unknown).

2. **`02` — bounds and powers refuse to guess.** `min` and `max` are how a row states a cap on one of
   its own figures, and returning the other side when one is unknown would silently un-cap it. `pow`
   goes through `double` and comes back through a checked conversion, so overflow and NaN are
   unknown rather than an exception inside a total somebody was only reading.

3. **`03` — a duration needs both ends.** `days_between` on a record whose end date is not set is not
   a very long time and not zero. Fractional for a `datetime`, whole for a `date`.

4. **`04` — a comparison can say "cannot say".** The sharp one. Two blank number fields compare as
   `0 < 0`, which is **false** — a definite answer, because blanks are zeros. But a comparison over
   an unknown is **unknown**, which is neither true nor false. A bare `<` on two nullables in C#
   answers false for both, which would make a rule keyed on "is the balance below the threshold" fire
   on every record whose balance has not been computed yet.

5. **`05` — unknown survives boolean logic.** Written because it did not. `Compare` answered null
   carefully and then `not`, `and`, `or` and `==` coerced it straight back to false, so
   `not ((a / b) < 1)` on a blank divisor answered **true** — the evaluator had quietly decided that
   a figure nobody could work out was below the threshold. The rule is three-valued logic, which also
   keeps the answers that are still definite: unknown AND false is false whatever the unknown turns
   out to be, and unknown OR true is true.

## Format

```json
{
  "name": "what this file settles",
  "why": "why it is worth pinning",
  "fields": {
    "revenue": { "type": "decimal" },
    "months":  { "type": "integer", "required": true },
    "start":   { "type": "datetime" }
  },
  "cases": [
    {
      "why": "optional, per case",
      "record": { "revenue": 100, "months": 4 },
      "expr":   "revenue / months",
      "expect": 25
    }
  ]
}
```

`fields` declares the entity the expression is written against. It is needed and not optional: an
identifier's TYPE decides what its comparisons mean, and whether a field is `required` decides
whether the generated C# reads it as `r.X` or as `(r.X ?? 0m)`.

`record` is the record as the database holds it; a key left out is a field nobody has filled in.
`expr` is the definition's own string. `expect` is the answer every implementation must give — a JSON
number, `true`, `false`, or `null` for unknown.

## Adding one

Add a case whenever a figure surprises you, and write down in `why` what the surprise was. That
sentence is the actual deliverable — the assertion only stops the regression, while the sentence is
what stops somebody "fixing" the behaviour back.

Both suites pick up new files automatically:

- `dotnet test oss/cordango/Cordango.slnx` — the generated source code, built and run
- `dotnet test backend/AppBuilder.slnx` — the platform's evaluator

**The two implementations are not two evaluators, and that is the difference from the permission and
condition fixtures.** One of them is SOURCE CODE. `Cordango.Compiler` describes the language — the
grammar, the parser, the typed AST, the author-time checking — and deliberately cannot run an
expression. The CLI translates one into a C# method compiled into the application it generates; the
platform works one out over a record, in `AppBuilder.Runtime.ComputedEval`. The line is the job, not
the feature.

So the two things held to these files are the emitted arithmetic and the platform evaluator, and
neither can borrow the other's answers.

## How each side is held to them

- **`GeneratedComputedTests`** (this repository) derives a one-entity application from these very
  files — one computed field per case — compiles it with `dotnet build`, loads the assembly and
  invokes each generated method. It is the only test that can say what the generated arithmetic
  actually answers, because a generated application carries no expression, no parser and no fixture.
- **`ComputedFixtureTests`** (this repository) checks that the emitter produces something for every
  shape rather than silently refusing it. Emitting is not compiling: a refused expression leaves a
  column permanently empty, which looks exactly like a column whose inputs are empty.
- **`ComputedFixtureTests`** (the platform) drives the same cases through `ComputedEval`.

The fixtures are hand-written from the rules above, never recorded from either side — the same
discipline the [permission fixtures](../permissions/) state, and for the same reason: recording would
make one implementation the definition of correct and enshrine its bugs as the contract the other has
to match.

## What these still do not pin

`prev()` and rollup windows. Both need an ordered series or a set of related records rather than one
row, so a case for them needs a different harness than a single record and a single expression. They
belong with the recompute cascade.

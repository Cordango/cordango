## What this changes

<!-- And why. If it fixes an issue, link it: Fixes #123 -->

## Does it change what the generator emits?

<!--
Delete this section if not. If it does, say whether that is intentional and how you checked — the
quickest way is to generate the corpus before and after and diff.
-->

## Checklist

- [ ] `dotnet test Cordango.slnx` passes
- [ ] Anything the generator cannot fully translate is reported with a diagnostic rather than
      partially emitted
- [ ] New behaviour has a test that exercises the generated output, not only the emitter
- [ ] Comments say *why*, where the reason is not obvious from the code

// Named records: what `as:` on a split or a repeat means.
//
// `{{po.id}}` reads the purchase order a split has selected, from anywhere inside it — including from
// inside a repeat nested in it, where `record` has already come to mean the repeated row. The name is
// the only way to reach past that shadowing, and it has to reach through components the page did not
// write, so it travels by provide/inject rather than as one more prop threaded through every block.
//
// Names nest the way the blocks do. A repeat inside a split sees the split's name and its own; the
// split's list, which is outside both, sees neither.

import { computed, inject, provide, unref } from 'vue'

const SCOPES = Symbol('cordango:scopes')

/** Every name in scope where this is called, as a ref. Empty rather than null, so it spreads. */
export function useScopes() {
  return inject(SCOPES, computed(() => ({})))
}

/**
 * Name `record` for everything beneath the caller, keeping every name from further out.
 *
 * No name provides the outer set unchanged: a repeat without `as` must not hide the names of the
 * split it sits in.
 */
export function provideScope(name, record) {
  const outer = useScopes()
  const scopes = computed(() => (name ? { ...outer.value, [name]: unref(record) } : outer.value))
  provide(SCOPES, scopes)
  return scopes
}

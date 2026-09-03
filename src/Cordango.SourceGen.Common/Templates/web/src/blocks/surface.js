import { inject, provide } from 'vue'

/**
 * How deep in cards a block already is.
 *
 * <p><b>The problem this solves.</b> A definition nests containers freely — a `card` block holding a
 * `stat`, or holding a `table`. Both of those leaves also drew a card of their own, so the screen
 * rendered a bordered box inside a bordered box: an outer card captioned "On today's list" wrapping
 * an inner card 168px tall containing the single character "0". Every card on every generated screen
 * was doubled, and it read as broken spacing rather than as a nesting mistake.</p>
 *
 * <p>Neither end can decide this alone. The container does not know what its children draw, and the
 * leaf does not know what wrapped it — so the container says "you are inside a card now" and the
 * leaf asks before drawing one. That is exactly what provide/inject is for.</p>
 *
 * <p>Call it from any block that draws a surface. A zero answer means it is the outermost one and
 * should draw the card; anything else means the card is already there and it should draw only its
 * contents.</p>
 */
const DEPTH = Symbol('cordango.surface')

export function useSurface() {
  const depth = inject(DEPTH, 0)

  // Descendants are inside a card either way: either this component just drew one, or it is itself
  // inside one it chose not to redraw.
  provide(DEPTH, depth + 1)

  return depth
}

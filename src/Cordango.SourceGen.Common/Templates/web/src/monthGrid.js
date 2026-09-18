// The month grid, as arithmetic.
//
// Two surfaces draw one: a `calendar` block over one entity's records, and the personal calendar
// over everything in this application that belongs to you. They arrange different rows, and they
// arrange them into exactly the same forty-two cells — so the cells are worked out here once rather
// than in both, which is the difference between "the two calendars disagree about which week a date
// is in" being impossible and being a bug somebody eventually files.

/** A date as `YYYY-MM-DD`, in the reader's own timezone. */
export const iso = (at) =>
  `${at.getFullYear()}-${String(at.getMonth() + 1).padStart(2, '0')}-${String(at.getDate()).padStart(2, '0')}`

/**
 * The Monday the grid starts on.
 *
 * Monday-first, and always six rows below it. A grid that changed height as you paged through the
 * year would make everything under it jump, which reads as the page reloading.
 */
export function gridStart(cursor) {
  const first = new Date(cursor.getFullYear(), cursor.getMonth(), 1)
  const weekday = (first.getDay() + 6) % 7
  return new Date(first.getFullYear(), first.getMonth(), 1 - weekday)
}

/** The day AFTER the last cell — an exclusive upper bound, which is what a range query wants. */
export function gridEnd(cursor) {
  const at = gridStart(cursor)
  at.setDate(at.getDate() + 42)
  return at
}

/** Forty-two cells: key, the number to print, whether it belongs to another month, whether it is today. */
export function gridDays(cursor) {
  const from = gridStart(cursor)
  const today = iso(new Date())

  return Array.from({ length: 42 }, (_, i) => {
    const at = new Date(from)
    at.setDate(at.getDate() + i)
    return {
      key: iso(at),
      day: at.getDate(),
      outside: at.getMonth() !== cursor.getMonth(),
      today: iso(at) === today,
    }
  })
}

/** Weekday headings, Monday first, in the reader's locale. */
export function weekdayNames() {
  const monday = new Date(2024, 0, 1)
  return Array.from({ length: 7 }, (_, i) => {
    const at = new Date(monday)
    at.setDate(at.getDate() + i)
    return at.toLocaleDateString(undefined, { weekday: 'short' })
  })
}

export const monthLabel = (cursor) =>
  cursor.toLocaleDateString(undefined, { month: 'long', year: 'numeric' })

/** The first of the month `by` months away. Always the first, so paging from the 31st does not skip
 *  February. */
export const shiftMonth = (cursor, by) =>
  new Date(cursor.getFullYear(), cursor.getMonth() + by, 1)

/**
 * Rows bucketed by the day they cover.
 *
 * One pass over the rows rather than a filter per cell: forty-two cells each scanning a month of
 * records is the kind of cost that only shows up once somebody has real data. A row with an end
 * lands in every day from its start to its end; one without lands in one.
 */
export function bucketByDay(rows, startOf, endOf) {
  const buckets = {}

  for (const row of rows) {
    const start = String(startOf(row) ?? '').slice(0, 10)
    if (!start) continue

    const end = String(endOf?.(row) ?? '').slice(0, 10) || start
    const at = new Date(start)
    const stop = new Date(end >= start ? end : start)

    while (iso(at) <= iso(stop)) {
      (buckets[iso(at)] ||= []).push(row)
      at.setDate(at.getDate() + 1)
    }
  }

  return buckets
}

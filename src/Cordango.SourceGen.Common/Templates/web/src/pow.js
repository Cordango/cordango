// Solving the server's proof-of-work challenge.
//
// Find a suffix whose SHA-256(token + suffix) begins with `difficulty` zero bits. About 200ms at 18
// bits — invisible next to typing a name, and four to five orders of magnitude more expensive than
// an empty POST for anything spraying a script at the endpoint.
//
// Yielding to the event loop every few thousand attempts rather than running in a Worker: a Worker
// would need its own bundle entry for a loop that finishes before most people have read the first
// question, and a frozen tab for 200ms is what the yield already prevents.
const encoder = new TextEncoder()

function hasLeadingZeroBits(bytes, bits) {
  const whole = bits >> 3
  for (let i = 0; i < whole; i++) if (bytes[i] !== 0) return false
  const rest = bits & 7
  return rest === 0 || (bytes[whole] >> (8 - rest)) === 0
}

/**
 * @param {string} token      the server's opaque challenge
 * @param {number} difficulty leading zero BITS required
 * @param {number} budgetMs   give up after this long — a slow phone must not hang forever
 * @returns {Promise<string>} the solution to send back
 */
export async function solve(token, difficulty, budgetMs = 20000) {
  const started = performance.now()
  for (let n = 0; ; n++) {
    const digest = new Uint8Array(
      await crypto.subtle.digest('SHA-256', encoder.encode(token + String(n))))
    if (hasLeadingZeroBits(digest, difficulty)) return String(n)

    // Breathe, so the page stays responsive and the budget is actually checkable.
    if ((n & 0x3ff) === 0) {
      if (performance.now() - started > budgetMs) {
        throw Object.assign(
          new Error('This is taking too long on this device. Please try again.'),
          { code: 'powTimeout' })
      }
      await new Promise((resolve) => setTimeout(resolve, 0))
    }
  }
}

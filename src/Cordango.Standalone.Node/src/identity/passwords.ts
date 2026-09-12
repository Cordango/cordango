// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

import { randomBytes, scrypt as scryptCallback, timingSafeEqual } from "node:crypto";
import { promisify } from "node:util";

const scrypt = promisify(scryptCallback) as (
  password: string,
  salt: Buffer,
  keylen: number,
  options: { N: number; r: number; p: number; maxmem: number },
) => Promise<Buffer>;

/**
 * How a password is stored, and why it is scrypt from the standard library.
 *
 * **No dependency.** Sign-in is the part of a system most damaging to get subtly wrong, and a
 * generated application should not be one `npm audit` away from a broken login. argon2 and bcrypt
 * are both native addons that have to compile on the machine that installs them; `node:crypto`'s
 * scrypt is in every Node this package supports, is the same primitive, and is memory-hard in the
 * way a plain hash is not.
 *
 * **Versioned on the wire.** The stored string names its own parameters, so raising the cost later
 * verifies existing passwords under the parameters they were written with and re-hashes on the
 * next successful sign-in. A scheme that does not carry its parameters can never be changed.
 */
const parameters = { N: 2 ** 15, r: 8, p: 1, maxmem: 96 * 1024 * 1024 } as const;
const keyLength = 64;
const saltLength = 16;

/** The minimum this application accepts. The same number the .NET target's Identity options set,
 * because the two produce the same application and a password that works on one must work on the
 * other. */
export const minimumPasswordLength = 12;

/** `scrypt$N$r$p$salt$hash`, all base64url. Self-describing, so the cost can be raised later. */
export async function hashPassword(password: string): Promise<string> {
  const salt = randomBytes(saltLength);
  const derived = await scrypt(password, salt, keyLength, parameters);
  return [
    "scrypt",
    parameters.N,
    parameters.r,
    parameters.p,
    salt.toString("base64url"),
    derived.toString("base64url"),
  ].join("$");
}

/**
 * Does this password produce the stored hash?
 *
 * Never throws — a malformed or truncated stored hash answers "no". Throwing here would turn one
 * corrupt row into a 500 on a login form, which tells an attacker that the row exists.
 */
export async function verifyPassword(password: string, stored: string | null): Promise<boolean> {
  if (stored === null || stored === "") return false;

  const parts = stored.split("$");
  if (parts.length !== 6 || parts[0] !== "scrypt") return false;

  const cost = Number(parts[1]);
  const blockSize = Number(parts[2]);
  const parallelism = Number(parts[3]);
  if (!Number.isInteger(cost) || !Number.isInteger(blockSize) || !Number.isInteger(parallelism)) return false;

  let salt: Buffer;
  let expected: Buffer;
  try {
    salt = Buffer.from(parts[4] ?? "", "base64url");
    expected = Buffer.from(parts[5] ?? "", "base64url");
  } catch {
    return false;
  }
  if (salt.length === 0 || expected.length === 0) return false;

  let derived: Buffer;
  try {
    derived = await scrypt(password, salt, expected.length, {
      N: cost,
      r: blockSize,
      p: parallelism,
      // Read from the stored record, so a hash written under a higher cost still verifies.
      maxmem: Math.max(parameters.maxmem, 256 * cost * blockSize * 2),
    });
  } catch {
    return false;
  }

  return derived.length === expected.length && timingSafeEqual(derived, expected);
}

/** True when the stored hash was written under parameters weaker than today's, so a successful
 * sign-in is the moment to write it again. */
export function needsRehash(stored: string | null): boolean {
  if (stored === null || stored === "") return true;
  const parts = stored.split("$");
  if (parts.length !== 6 || parts[0] !== "scrypt") return true;
  return Number(parts[1]) < parameters.N;
}

/**
 * What is wrong with this password, or null when nothing is.
 *
 * The sentence NAMES THE RULE that was broken, and that is deliberate: "Passwords must be at least
 * 12 characters." is something a person can act on, and a generic "invalid" is not. These codes
 * are left out of the message tables on purpose so the specific sentence survives translation.
 */
export function checkPassword(password: string | undefined | null): string | null {
  if (password === undefined || password === null || password === "")
    return "Enter a password.";
  if (password.length < minimumPasswordLength)
    return `Passwords must be at least ${minimumPasswordLength} characters.`;
  return null;
}

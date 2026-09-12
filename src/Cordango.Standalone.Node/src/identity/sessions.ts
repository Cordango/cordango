// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

import { createHmac, timingSafeEqual } from "node:crypto";
import type { Request, Response } from "express";
import { clearCookie, cookie, setCookie } from "../http/cookies.js";

/**
 * The session cookie: who you are, signed, with no server-side table behind it.
 *
 * **Why stateless.** A session table would have to be read on every request, swept for expiry, and
 * kept consistent across however many instances of the application are running. The thing it buys
 * — revoking one session without touching the others — is not what a generated application
 * actually needs; what it needs is "this account's sessions all stop now", which the security
 * stamp already does, from the one row that decides it.
 *
 * **The stamp is the revocation.** The cookie carries the account's security stamp as it was when
 * the cookie was minted. A password change, a reset, a lockout or a role change moves the stamp,
 * and every cookie minted under the old one stops verifying on its next request — on every device
 * at once, with nothing to sweep.
 */
export interface SessionOptions {
  /** The cookie's name. Per application, so two generated applications on one host do not sign
   * each other's users in and out. */
  readonly cookieName: string;
  /** The key the signature is taken under. Rotating it signs everybody out and nothing worse. */
  readonly secret: string;
  /** `Secure` on the cookie. Off on a plain-HTTP dev origin, where the browser would store nothing
   * and the symptom is a login that appears to succeed and leaves you signed out. */
  readonly secure: boolean;
  /** How long a session lasts. Fourteen days, sliding, as the .NET target's cookie does. */
  readonly lifetimeDays?: number;
}

/** What a verified cookie says. */
export interface SessionTicket {
  readonly userId: string;
  readonly stamp: string;
  readonly issuedAt: number;
  /** True when the person asked to stay signed in. A session cookie otherwise, which the browser
   * drops when it closes. */
  readonly persistent: boolean;
}

const defaultLifetimeDays = 14;

/** Mint a cookie for this account and put it on the response. */
export function signIn(
  response: Response,
  options: SessionOptions,
  ticket: { userId: string; stamp: string; persistent: boolean },
): void {
  const payload = encode({ ...ticket, issuedAt: Date.now() });
  const days = options.lifetimeDays ?? defaultLifetimeDays;

  setCookie(response, options.cookieName, `${payload}.${sign(options.secret, payload)}`, {
    httpOnly: true,
    secure: options.secure,
    sameSite: "Lax",
    // A persistent session survives the browser closing; a non-persistent one deliberately does
    // not carry Max-Age at all, which is what makes it a session cookie.
    ...(ticket.persistent ? { maxAge: days * 24 * 60 * 60 } : {}),
  });
}

export function signOut(response: Response, options: SessionOptions): void {
  clearCookie(response, options.cookieName, {
    httpOnly: true,
    secure: options.secure,
    sameSite: "Lax",
  });
}

/**
 * The ticket this request carries, or null.
 *
 * Null covers every way a request can fail to prove who it is — no cookie, a tampered one, one
 * signed under a rotated key, one past its lifetime. They are one answer on purpose: a caller that
 * could tell them apart would be a way to probe the signing key.
 */
export function readTicket(request: Request, options: SessionOptions): SessionTicket | null {
  const raw = cookie(request, options.cookieName);
  if (raw === undefined || raw === "") return null;

  const separator = raw.lastIndexOf(".");
  if (separator <= 0) return null;

  const payload = raw.slice(0, separator);
  const signature = raw.slice(separator + 1);

  const expected = sign(options.secret, payload);
  const left = Buffer.from(signature);
  const right = Buffer.from(expected);
  if (left.length !== right.length || !timingSafeEqual(left, right)) return null;

  const ticket = decode(payload);
  if (ticket === null) return null;

  const days = options.lifetimeDays ?? defaultLifetimeDays;
  if (Date.now() - ticket.issuedAt > days * 24 * 60 * 60 * 1000) return null;

  return ticket;
}

/**
 * Sliding expiry: re-mint a cookie that is more than half way through its life.
 *
 * Half way rather than on every request, because a `Set-Cookie` on every response is a header on
 * every response, and a session that is re-signed constantly can never be reasoned about from a
 * capture.
 */
export function slide(response: Response, options: SessionOptions, ticket: SessionTicket): void {
  const days = options.lifetimeDays ?? defaultLifetimeDays;
  const lifetime = days * 24 * 60 * 60 * 1000;
  if (Date.now() - ticket.issuedAt < lifetime / 2) return;

  signIn(response, options, {
    userId: ticket.userId,
    stamp: ticket.stamp,
    persistent: ticket.persistent,
  });
}

function sign(secret: string, payload: string): string {
  return createHmac("sha256", secret).update(payload).digest("base64url");
}

function encode(ticket: SessionTicket): string {
  return Buffer.from(
    JSON.stringify({ u: ticket.userId, s: ticket.stamp, i: ticket.issuedAt, p: ticket.persistent }),
    "utf8",
  ).toString("base64url");
}

function decode(payload: string): SessionTicket | null {
  try {
    const parsed: unknown = JSON.parse(Buffer.from(payload, "base64url").toString("utf8"));
    if (typeof parsed !== "object" || parsed === null) return null;

    const record = parsed as Record<string, unknown>;
    const userId = record["u"];
    const stamp = record["s"];
    const issuedAt = record["i"];
    if (typeof userId !== "string" || typeof stamp !== "string" || typeof issuedAt !== "number")
      return null;

    return { userId, stamp, issuedAt, persistent: record["p"] === true };
  } catch {
    return null;
  }
}

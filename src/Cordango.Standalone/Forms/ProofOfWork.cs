// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace Cordango.Standalone.Forms;

/// <summary>A challenge to solve before a public write is accepted.</summary>
public sealed record PowChallenge(string Token, int Difficulty, DateTimeOffset ExpiresAt);

/// <summary>
/// Making an anonymous write cost something.
///
/// <para><b>The free kind of bot defence, deliberately.</b> A hidden honeypot field, a minimum fill
/// time, and this — no third party. A hosted anti-bot widget would mean shipping visitor IPs and
/// browser signals to a vendor from a page anyone can open, which is a data-processor relationship
/// and a line in a privacy policy, in exchange for stopping spam three cheap checks already stop.</para>
///
/// <para><b>Not yet here: a rate limit.</b> The three checks above cost an attacker time per
/// submission and nothing per attempt at the GET, so a determined script can still read a published
/// form as often as it likes. Add ASP.NET's rate limiter on this controller before a generated
/// application with a public form goes somewhere it can be found.</para>
///
/// <para>The challenge is a SEALED artifact rather than a row in a table: it carries its own expiry
/// and is signed with the application's data-protection key, so verifying one needs no storage and no
/// cleanup job. That is also what makes the minimum age enforceable without trusting a client clock —
/// the issue time is derivable from the expiry, and forging it means forging the seal.</para>
/// </summary>
public sealed class ProofOfWork
{
    /// <summary>Leading zero BITS a solution must produce. About 200ms in a browser, and four to five
    /// orders of magnitude more than an empty POST costs a script.</summary>
    public const int Difficulty = 18;

    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(15);

    /// <summary>How OLD a challenge must be before it may be spent. A submission arriving in the same
    /// breath as its challenge was not typed by a person.</summary>
    public static readonly TimeSpan MinAge = TimeSpan.FromSeconds(3);

    public enum Verdict { Ok, Missing, Bad, Expired, TooFast, WrongSubject }

    private readonly IDataProtector _protector;
    private readonly TimeProvider _clock;

    public ProofOfWork(IDataProtectionProvider protection, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(protection);
        _protector = protection.CreateProtector("cordango.public-surface-pow.v1");
        _clock = clock;
    }

    /// <summary>Bound to (surface, id): a challenge minted for one form cannot be spent on another,
    /// even where the two addresses collide.</summary>
    public PowChallenge Issue(string surface, string id)
    {
        var expires = _clock.GetUtcNow().Add(Ttl);
        var payload = $"{surface}\n{id}\n{expires.ToUnixTimeSeconds()}\n{Guid.NewGuid():N}";
        return new PowChallenge(_protector.Protect(payload), Difficulty, expires);
    }

    public Verdict Verify(string surface, string id, string? token, string? solution, TimeSpan? minAge = null)
    {
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(solution)) return Verdict.Missing;

        string payload;
        try { payload = _protector.Unprotect(token); }
        catch (CryptographicException) { return Verdict.Bad; }

        var parts = payload.Split('\n');
        if (parts.Length != 4) return Verdict.Bad;
        if (!string.Equals(parts[0], surface, StringComparison.Ordinal)
            || !string.Equals(parts[1], id, StringComparison.Ordinal)) return Verdict.WrongSubject;
        if (!long.TryParse(parts[2], out var epoch)) return Verdict.Bad;

        var expires = DateTimeOffset.FromUnixTimeSeconds(epoch);
        var now = _clock.GetUtcNow();
        if (now > expires) return Verdict.Expired;

        // Issue time is derivable rather than stored: the TTL is a constant, so `expires - Ttl` is
        // when this was minted, and it is inside the sealed payload.
        if (minAge is { } floor && now - (expires - Ttl) < floor) return Verdict.TooFast;

        return HasLeadingZeroBits(SHA256.HashData(Encoding.UTF8.GetBytes(token + solution)), Difficulty)
            ? Verdict.Ok
            : Verdict.Bad;
    }

    private static bool HasLeadingZeroBits(ReadOnlySpan<byte> hash, int bits)
    {
        var whole = bits >> 3;
        for (var i = 0; i < whole; i++) if (hash[i] != 0) return false;
        var rest = bits & 7;
        return rest == 0 || (hash[whole] >> (8 - rest)) == 0;
    }
}

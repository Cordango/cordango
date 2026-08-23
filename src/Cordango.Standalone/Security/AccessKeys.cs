// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Security.Cryptography;
using System.Text;
using Cordango.Standalone.Data;
using Cordango.Standalone.Http;
using Cordango.Standalone.Records;
using Microsoft.EntityFrameworkCore;

namespace Cordango.Standalone.Security;

/// <summary>
/// A credential a program can carry.
///
/// <para><b>Why this exists at all.</b> Everything else about signing in here is a browser: a session
/// cookie, and an antiforgery token that only a browser can be asked to echo. That is the right
/// design for the screens, and it locks out every caller that is not one — a script, a CI job, and
/// the MCP endpoint, which is a program acting for a person by definition.</para>
///
/// <para><b>It is NOT an <see cref="IRecord"/>, deliberately.</b> A record gets a store, a gateway,
/// a controller and a place in the MCP tool surface. This table must have none of those: a caller who
/// could list access keys through the same door they came in by has found a way to enumerate every
/// other credential in the application. It is reachable only through <see cref="IAccessKeys"/>, and
/// every method there is scoped to one owner.</para>
/// </summary>
public sealed class AccessKey
{
    /// <summary>The public half, which travels in the token in the clear. It is a lookup key and
    /// nothing else — knowing it proves nothing.</summary>
    public string Id { get; set; } = "";

    /// <summary>What the owner called it, so a list of four keys is a list of four decisions rather
    /// than four opaque strings.</summary>
    public string Label { get; set; } = "";

    /// <summary>The login this key acts as. A key is never more than its owner.</summary>
    public string UserId { get; set; } = "";

    /// <summary>SHA-256 of the secret half, hex. The secret itself is shown once, at minting, and is
    /// then unrecoverable — a store that could show a key again is a store that leaks every key when
    /// it is read.</summary>
    public string SecretHash { get; set; } = "";

    public DateTimeOffset Created { get; set; }

    /// <summary>When it was last accepted. The one field that makes "which of these can I safely
    /// delete" answerable.</summary>
    public DateTimeOffset? LastUsed { get; set; }

    /// <summary>Optional. A key with no expiry is a key somebody has to remember to remove.</summary>
    public DateTimeOffset? Expires { get; set; }
}

/// <summary>Minting, listing and revoking access keys, always for ONE owner.</summary>
public interface IAccessKeys
{
    /// <summary>
    /// A new key. The returned token is the ONLY time the secret exists outside the caller's hands.
    /// </summary>
    Task<(AccessKey Key, string Token)> MintAsync(
        string userId, string label, DateTimeOffset? expires, CancellationToken ct);

    /// <summary>This owner's keys, without anything secret in them.</summary>
    Task<IReadOnlyList<AccessKey>> ListAsync(string userId, CancellationToken ct);

    /// <summary>Delete one key, if it is this owner's. Answers false rather than throwing when it is
    /// not — a caller probing for other people's key ids learns nothing either way.</summary>
    Task<bool> RevokeAsync(string userId, string keyId, CancellationToken ct);

    /// <summary>The login a token belongs to, or null if it is unknown, expired or wrong.</summary>
    Task<string?> VerifyAsync(string token, CancellationToken ct);
}

/// <summary>
/// The token format, and the crypto around it.
///
/// <para><b>Two halves, and only one of them is stored.</b> A token is
/// <c>cordango_pat.&lt;id&gt;.&lt;secret&gt;</c>: the id finds the row, the secret is compared
/// against a hash. A single opaque string would mean either scanning every row and hashing against
/// each — which is a timing oracle and does not scale — or storing something reversible.</para>
///
/// <para><b>SHA-256, not a password hash, and that is not an oversight.</b> Slow hashing exists to
/// make guessing a LOW-ENTROPY human-chosen secret expensive. This secret is 32 bytes from the
/// system CSPRNG; there is nothing to guess, and a per-request key derivation would only make every
/// legitimate call slower.</para>
/// </summary>
public static class AccessKeyToken
{
    /// <summary>Recognisable at a glance, so a secret scanner and a person reading a log both know
    /// what they are looking at. <c>_pat</c> for "personal access token", leaving room for other
    /// kinds later.</summary>
    public const string Prefix = "cordango_pat";

    /// <summary>A fresh pair. The id is short because it is only a lookup; the secret is 32 bytes
    /// because it is the whole of the security.</summary>
    public static (string Id, string Secret, string Token) Mint()
    {
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var secret = Base64Url(RandomNumberGenerator.GetBytes(32));

        return (id, secret, $"{Prefix}.{id}.{secret}");
    }

    /// <summary>Split a presented token. Shape only — whether it is VALID is a question for the
    /// store, and answering shape here keeps a malformed header from ever reaching the database.</summary>
    public static bool TryParse(string? token, out string id, out string secret)
    {
        id = "";
        secret = "";

        if (string.IsNullOrEmpty(token)) return false;

        var parts = token.Split('.');
        if (parts.Length != 3 || parts[0] != Prefix) return false;
        if (parts[1].Length == 0 || parts[2].Length == 0) return false;

        id = parts[1];
        secret = parts[2];
        return true;
    }

    public static string Hash(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();

    /// <summary>Compare in time that does not depend on where the two differ. Overkill for a hash
    /// comparison and free, which is the right trade for the one line that decides whether a request
    /// is authenticated.</summary>
    public static bool Matches(string presented, string stored) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(Hash(presented)), Encoding.UTF8.GetBytes(stored));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>The store, over the application's own database.</summary>
public sealed class AccessKeys : IAccessKeys
{
    /// <summary>Enough for a person to tell four keys apart; short enough that it cannot be used to
    /// smuggle anything into a page that renders it.</summary>
    private const int MaxLabel = 80;

    private readonly CordangoDbContext _db;
    private readonly IClock _clock;

    public AccessKeys(CordangoDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<(AccessKey Key, string Token)> MintAsync(
        string userId, string label, DateTimeOffset? expires, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new RecordException("auth.required", "Sign in before minting an access key.", 401);

        var trimmed = (label ?? "").Trim();
        if (trimmed.Length == 0)
            throw new RecordException("key.label_required", "Give the key a name you will recognise later.");
        if (trimmed.Length > MaxLabel)
            throw new RecordException("key.label_too_long", $"A key's name may be at most {MaxLabel} characters.");

        if (expires is { } when && when <= _clock.UtcNow)
            throw new RecordException("key.expiry_in_past", "An expiry date has to be in the future.");

        var (id, secret, token) = AccessKeyToken.Mint();

        var key = new AccessKey
        {
            Id = id,
            Label = trimmed,
            UserId = userId,
            SecretHash = AccessKeyToken.Hash(secret),
            Created = _clock.UtcNow,
            Expires = expires,
        };

        _db.Set<AccessKey>().Add(key);
        await _db.SaveChangesAsync(ct);

        return (key, token);
    }

    public async Task<IReadOnlyList<AccessKey>> ListAsync(string userId, CancellationToken ct) =>
        await _db.Set<AccessKey>()
            .Where(k => k.UserId == userId)
            .OrderByDescending(k => k.Created)
            .AsNoTracking()
            .ToListAsync(ct);

    public async Task<bool> RevokeAsync(string userId, string keyId, CancellationToken ct)
    {
        // Scoped by OWNER as well as by id, in the query rather than after it. Fetching by id and
        // then checking the owner is the same logic with a window in it, and it is the window that
        // gets refactored away.
        var key = await _db.Set<AccessKey>()
            .FirstOrDefaultAsync(k => k.Id == keyId && k.UserId == userId, ct);

        if (key is null) return false;

        _db.Set<AccessKey>().Remove(key);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<string?> VerifyAsync(string token, CancellationToken ct)
    {
        if (!AccessKeyToken.TryParse(token, out var id, out var secret)) return null;

        var key = await _db.Set<AccessKey>().FirstOrDefaultAsync(k => k.Id == id, ct);
        if (key is null) return null;

        if (key.Expires is { } expires && expires <= _clock.UtcNow) return null;
        if (!AccessKeyToken.Matches(secret, key.SecretHash)) return null;

        // Written at most once a minute. Every accepted request would otherwise be a write, which
        // turns a read-only workload into one that contends on this row — and "last used" to the
        // second is not worth that to anybody.
        var now = _clock.UtcNow;
        if (key.LastUsed is null || now - key.LastUsed > TimeSpan.FromMinutes(1))
        {
            key.LastUsed = now;
            await _db.SaveChangesAsync(ct);
        }

        return key.UserId;
    }
}

/// <summary>The table.</summary>
public static class AccessKeySchema
{
    public static ModelBuilder AddAccessKeys(this ModelBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Entity<AccessKey>(entity =>
        {
            entity.ToTable("access_key");
            entity.HasKey(k => k.Id);
            entity.Property(k => k.Id).HasColumnName("id").HasMaxLength(64);
            entity.Property(k => k.Label).HasColumnName("label").HasMaxLength(80).IsRequired();
            entity.Property(k => k.UserId).HasColumnName("user_id").HasMaxLength(64).IsRequired();
            entity.Property(k => k.SecretHash).HasColumnName("secret_hash").HasMaxLength(64).IsRequired();
            entity.Property(k => k.Created).HasColumnName("created");
            entity.Property(k => k.LastUsed).HasColumnName("last_used");
            entity.Property(k => k.Expires).HasColumnName("expires");

            // Every request that presents a key finds it by id, which is the primary key already;
            // this is the one that makes the settings screen cheap. Unnamed, so EF's own convention
            // applies — the generated migration names it by the same convention, and a name set on
            // one side only is a difference the migration fidelity test reports.
            entity.HasIndex(k => k.UserId);
        });

        return builder;
    }
}

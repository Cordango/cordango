// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cordango.AccessTokens;

/// <summary>Which of the two credentials a user asked their workspace to mint.</summary>
public enum CordTokenKind
{
    /// <summary>A Personal Access Token: the secret alone. The holder must also say WHICH instance
    /// it belongs to, because the token does not.</summary>
    Personal,

    /// <summary>A Cordango Access Exchange Token: the same secret, carrying the address of the
    /// instance and the tenant it was minted in, so `cord login &lt;token&gt;` needs nothing else.</summary>
    Exchange,
}

/// <summary>
/// The credential a person pastes into <c>cord login</c>.
///
/// <para><b>Two kinds, one secret.</b> Both spellings carry the identical credential — a key id and
/// 256 bits of CSPRNG secret. The exchange token adds an ADDRESS segment naming the instance URL and
/// tenant. That is the only difference, and keeping it to one segment is deliberate: the server's
/// verification path never branches on the kind, so there is no second code path where the weaker
/// one could be checked less carefully.</para>
///
/// <para><b>The address is a claim, never an authority.</b> Base64 is encoding, not encryption —
/// anybody holding an exchange token can read the URL and tenant out of it, and anybody MINTING one
/// could have written whatever they liked there. So the CLI treats it as a hint about where to go
/// and nothing more: it connects to the named instance, and the SERVER independently reports which
/// tenant the key actually belongs to. Login fails if the two disagree. A token cannot talk its way
/// into a tenant by describing itself as belonging there.</para>
///
/// <para><b>What the encoding does buy.</b> A fixed scannable prefix (<c>cord_pat.</c> /
/// <c>cord_cxt.</c>) so secret scanners and log scrubbers can find one; a checksum so a truncated or
/// mistyped paste fails locally with "that is not a whole token" instead of remotely as an
/// indistinguishable 401; and dot-separated segments that never collide with the base64url alphabet,
/// which is why the separator is <c>.</c> and not <c>_</c>.</para>
///
/// <para><b>The server stores only <see cref="HashSecret"/>.</b> The raw token exists in the reply to
/// the create call and nowhere else, so a database leak yields no usable credential.</para>
/// </summary>
/// <param name="Kind">Which spelling this is.</param>
/// <param name="KeyId">The public half — what the server looks the row up by, so verification is an
/// indexed read rather than a hash comparison against every key in the table.</param>
/// <param name="Secret">The private half. Compared against the stored hash in fixed time.</param>
/// <param name="InstanceUrl">Exchange tokens only: the origin the CLI should talk to.</param>
/// <param name="TenantId">Exchange tokens only: the workspace the key claims to belong to.</param>
public sealed record CordAccessToken(
    CordTokenKind Kind,
    string KeyId,
    string Secret,
    string? InstanceUrl = null,
    string? TenantId = null)
{
    /// <summary>Scannable, fixed, and different per kind so a human reading a support ticket can tell
    /// at a glance which one they were sent.</summary>
    public const string PersonalPrefix = "cord_pat";
    public const string ExchangePrefix = "cord_cxt";

    /// <summary>Bytes behind <see cref="KeyId"/>. Not a secret — it only has to be unique.</summary>
    private const int KeyIdBytes = 9;

    /// <summary>256 bits. The number that matters.</summary>
    private const int SecretBytes = 32;

    /// <summary>Checksum length in base64url characters. Enough to catch a truncated paste; it is a
    /// typo detector, not a security control, and nothing may treat it as one.</summary>
    private const int CheckLength = 8;

    /// <summary>Every token starts with one of these. Used by the API to decide whether an
    /// <c>Authorization: Bearer</c> value is one of ours before trying to parse it as a JWT.</summary>
    public static bool LooksLikeCordToken(string? value) =>
        value is not null
        && (value.StartsWith(PersonalPrefix + ".", StringComparison.Ordinal)
         || value.StartsWith(ExchangePrefix + ".", StringComparison.Ordinal));

    /// <summary>
    /// Mint a fresh credential.
    /// </summary>
    /// <param name="instanceUrl">Required for <see cref="CordTokenKind.Exchange"/>, ignored otherwise.</param>
    /// <param name="tenantId">Required for <see cref="CordTokenKind.Exchange"/>, ignored otherwise.</param>
    public static CordAccessToken Issue(CordTokenKind kind, string? instanceUrl = null, string? tenantId = null)
    {
        if (kind == CordTokenKind.Exchange)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(instanceUrl);
            ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        }

        return new CordAccessToken(
            kind,
            Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(KeyIdBytes)),
            Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(SecretBytes)),
            kind == CordTokenKind.Exchange ? NormalizeOrigin(instanceUrl!) : null,
            kind == CordTokenKind.Exchange ? tenantId : null);
    }

    /// <summary>The string the user copies. Call it once — nothing can recover it afterwards.</summary>
    public string Encode()
    {
        var body = Kind == CordTokenKind.Exchange
            ? string.Join('.', ExchangePrefix, KeyId, Secret, EncodeAddress(InstanceUrl!, TenantId!))
            : string.Join('.', PersonalPrefix, KeyId, Secret);

        return body + "." + Checksum(body);
    }

    /// <summary>
    /// Read a pasted token back.
    /// </summary>
    /// <param name="error">A sentence naming what is wrong with the string — never what is wrong with
    /// the CREDENTIAL. "This is not a whole token" is safe to print; "no such key" is the server's to
    /// say, after it has rate-limited the attempt.</param>
    public static bool TryParse(string? raw, out CordAccessToken? token, out string? error)
    {
        token = null;
        error = null;

        var text = raw?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            error = "no token was given";
            return false;
        }

        var parts = text.Split('.');
        if (parts.Length is < 4 or > 5)
        {
            error = "not a Cordango token — expected a `cord_pat.` or `cord_cxt.` value";
            return false;
        }

        var kind = parts[0] switch
        {
            PersonalPrefix => CordTokenKind.Personal,
            ExchangePrefix => CordTokenKind.Exchange,
            _ => (CordTokenKind?)null,
        };
        if (kind is null)
        {
            error = "not a Cordango token — expected a `cord_pat.` or `cord_cxt.` value";
            return false;
        }

        // A personal token has no address segment and an exchange token must have one. Checked before
        // the checksum so a personal token pasted where an exchange token was meant says so plainly.
        var expectedParts = kind == CordTokenKind.Exchange ? 5 : 4;
        if (parts.Length != expectedParts)
        {
            error = kind == CordTokenKind.Exchange
                ? "this exchange token is incomplete — it carries no instance address"
                : "this personal access token has a segment too many";
            return false;
        }

        // The checksum covers everything before it, so a token that lost characters anywhere fails
        // here rather than becoming a plausible-looking credential for a key that does not exist.
        var body = string.Join('.', parts[..^1]);
        if (!FixedTimeEquals(Checksum(body), parts[^1]))
        {
            error = "this token is damaged — it was probably truncated or mistyped when copied";
            return false;
        }

        if (kind == CordTokenKind.Personal)
        {
            token = new CordAccessToken(kind.Value, parts[1], parts[2]);
            return true;
        }

        if (!TryDecodeAddress(parts[3], out var url, out var tenant))
        {
            error = "this exchange token's instance address could not be read";
            return false;
        }

        token = new CordAccessToken(kind.Value, parts[1], parts[2], url, tenant);
        return true;
    }

    /// <summary>What the database stores in place of the secret: lower-case hex SHA-256. Hex rather
    /// than base64 so the column is greppable and diffable when somebody is debugging a key by
    /// hand.</summary>
    public static string HashSecret(string secret) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    /// <summary>
    /// Compare a presented secret against a stored hash without leaking, through timing, how much of
    /// it was right.
    /// </summary>
    public static bool SecretMatches(string presentedSecret, string storedHash) =>
        FixedTimeEquals(HashSecret(presentedSecret), storedHash);

    /// <summary>The token as it is safe to display after minting: enough to recognise which key a row
    /// is, useless as a credential. Shown in the key list beside the name.</summary>
    public static string Hint(CordTokenKind kind, string keyId) =>
        $"{(kind == CordTokenKind.Exchange ? ExchangePrefix : PersonalPrefix)}.{keyId}.…";

    // ---- encoding internals --------------------------------------------------------------------

    private static string Checksum(string body) =>
        Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))[..CheckLength];

    private static string EncodeAddress(string instanceUrl, string tenantId) =>
        Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(
            new JsonObject { ["u"] = instanceUrl, ["t"] = tenantId }));

    private static bool TryDecodeAddress(string segment, out string? url, out string? tenant)
    {
        url = null;
        tenant = null;
        try
        {
            if (JsonNode.Parse(Base64Url.DecodeFromChars(segment)) is not JsonObject doc) return false;
            url = (string?)doc["u"];
            tenant = (string?)doc["t"];
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(tenant)) return false;

        // A relative or non-HTTP address in the one field that decides where a credential gets SENT
        // is refused here rather than by whatever tries to use it. `file://` and `javascript:` are
        // the reason this is a scheme allow-list and not a `Uri.TryCreate` call.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            return false;

        url = NormalizeOrigin(url);
        return true;
    }

    /// <summary>Scheme, host and port, no trailing slash and no path — the shape every caller wants
    /// to concatenate a route onto. One derivation, so a token minted by one release and read by
    /// another cannot disagree about whether the origin ends in a slash.</summary>
    public static string NormalizeOrigin(string url)
    {
        var trimmed = url.Trim().TrimEnd('/');
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed)
            ? parsed.GetLeftPart(UriPartial.Authority)
            : trimmed;
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}

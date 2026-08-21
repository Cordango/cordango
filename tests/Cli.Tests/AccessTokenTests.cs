// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.AccessTokens;

namespace Cordango.Cli.Tests;

/// <summary>
/// The credential format, from both ends.
///
/// <para>These live in the CLI's test project rather than the API's on purpose: the CLI is the side
/// that PARSES what the server ENCODES, and a format only one side can read is the exact failure
/// this shared project exists to make impossible.</para>
/// </summary>
public sealed class AccessTokenTests
{
    [Fact]
    public void PersonalTokenRoundTrips()
    {
        var issued = CordAccessToken.Issue(CordTokenKind.Personal);

        Assert.True(CordAccessToken.TryParse(issued.Encode(), out var parsed, out var error));
        Assert.Null(error);
        Assert.Equal(CordTokenKind.Personal, parsed!.Kind);
        Assert.Equal(issued.KeyId, parsed.KeyId);
        Assert.Equal(issued.Secret, parsed.Secret);

        // A personal token says nothing about where it belongs — that is the whole difference, and
        // it is what makes `--instance` mandatory for one kind and not the other.
        Assert.Null(parsed.InstanceUrl);
        Assert.Null(parsed.TenantId);
    }

    [Fact]
    public void ExchangeTokenCarriesItsAddress()
    {
        var issued = CordAccessToken.Issue(CordTokenKind.Exchange, "https://acme.cordango.com/", "acme");

        Assert.True(CordAccessToken.TryParse(issued.Encode(), out var parsed, out _));
        Assert.Equal(CordTokenKind.Exchange, parsed!.Kind);
        Assert.Equal("acme", parsed.TenantId);
        // Normalized on the way in AND out, so a trailing slash cannot produce two spellings of one
        // instance in the credential file.
        Assert.Equal("https://acme.cordango.com", parsed.InstanceUrl);
    }

    [Theory]
    [InlineData(1)]   // a mangled key id
    [InlineData(2)]   // a mangled secret
    [InlineData(3)]   // a mangled address
    public void ATamperedSegmentFailsTheChecksum(int segment)
    {
        var raw = CordAccessToken.Issue(CordTokenKind.Exchange, "https://localhost:5215", "default").Encode();
        var parts = raw.Split('.');
        parts[segment] = parts[segment][..^1] + (parts[segment][^1] == 'A' ? 'B' : 'A');

        Assert.False(CordAccessToken.TryParse(string.Join('.', parts), out var parsed, out var error));
        Assert.Null(parsed);
        Assert.Contains("damaged", error);
    }

    [Fact]
    public void ATruncatedTokenIsRejectedLocally()
    {
        var raw = CordAccessToken.Issue(CordTokenKind.Personal).Encode();

        // The failure mode this checksum exists for: a copy that lost its tail. Without it this
        // reaches the server and comes back as an indistinguishable 401.
        Assert.False(CordAccessToken.TryParse(raw[..^4], out _, out var error));
        Assert.Contains("damaged", error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ghp_definitelyNotOurs")]
    [InlineData("cord_pat.onlyoneSegment")]
    public void NonTokensAreRefused(string raw)
    {
        Assert.False(CordAccessToken.TryParse(raw, out var parsed, out var error));
        Assert.Null(parsed);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void AnExchangeTokenNamingANonHttpAddressIsRefused()
    {
        // The address segment decides where a live credential gets SENT. `file://` and friends are
        // refused at parse time rather than by whatever would otherwise have tried to use it.
        var hostile = System.Buffers.Text.Base64Url.EncodeToString(
            System.Text.Encoding.UTF8.GetBytes("""{"u":"file:///etc/passwd","t":"acme"}"""));
        // Checksum-VALID around the hostile address, so the refusal is demonstrably about the
        // scheme rather than about a damaged string.
        var valid = ReChecksum($"{CordAccessToken.ExchangePrefix}.keyid.secret.{hostile}");

        Assert.False(CordAccessToken.TryParse(valid, out var parsed, out var error));
        Assert.Null(parsed);
        Assert.Contains("address", error);
    }

    [Fact]
    public void OnlyTheHashIsComparable()
    {
        var issued = CordAccessToken.Issue(CordTokenKind.Personal);
        var hash = CordAccessToken.HashSecret(issued.Secret);

        Assert.True(CordAccessToken.SecretMatches(issued.Secret, hash));
        Assert.False(CordAccessToken.SecretMatches(issued.Secret + "x", hash));
        // 64 lower-case hex characters: what the access_keys column stores.
        Assert.Equal(64, hash.Length);
        Assert.DoesNotContain(issued.Secret, hash);
    }

    [Fact]
    public void OnlyOurTokensClaimTheBearerSlot()
    {
        Assert.True(CordAccessToken.LooksLikeCordToken(
            CordAccessToken.Issue(CordTokenKind.Personal).Encode()));
        Assert.True(CordAccessToken.LooksLikeCordToken(
            CordAccessToken.Issue(CordTokenKind.Exchange, "https://x.test", "t").Encode()));

        // A JWT must fall through to OpenIddict's validation — the policy scheme's whole job.
        Assert.False(CordAccessToken.LooksLikeCordToken("eyJhbGciOiJSUzI1NiJ9.e30.sig"));
        Assert.False(CordAccessToken.LooksLikeCordToken(null));
    }

    /// <summary>Re-stamp a body's checksum, mirroring <see cref="CordAccessToken.Encode"/>. Lets a
    /// test build a well-formed token carrying content the parser should still refuse.</summary>
    private static string ReChecksum(string body)
    {
        var digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(body));
        return body + "." + System.Buffers.Text.Base64Url.EncodeToString(digest)[..8];
    }
}

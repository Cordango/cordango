// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.Standalone.Data;
using Cordango.Standalone.Http;
using Cordango.Standalone.Records;
using Cordango.Standalone.Security;
using Microsoft.EntityFrameworkCore;

namespace Cordango.Standalone.Tests;

public class AccessKeyTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 23, 10, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = Now;
    }

    private sealed class KeyDb : CordangoDbContext
    {
        public KeyDb(DbContextOptions<KeyDb> options, ICurrentUser user, IClock clock)
            : base(options, user, clock) { }

        protected override void ConfigureModel(ModelBuilder builder) => builder.AddAccessKeys();
    }

    private static (AccessKeys Keys, FixedClock Clock) Store()
    {
        var clock = new FixedClock();

        var options = new DbContextOptionsBuilder<KeyDb>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return (new AccessKeys(new KeyDb(options, new AnonymousUser(), clock), clock), clock);
    }

    [Fact]
    public async Task A_minted_key_verifies_back_to_its_owner()
    {
        var (keys, _) = Store();

        var (_, token) = await keys.MintAsync("user-1", "CI", null, default);

        Assert.Equal("user-1", await keys.VerifyAsync(token, default));
    }

    [Fact]
    public async Task The_token_is_the_only_place_the_secret_ever_appears()
    {
        var (keys, _) = Store();

        var (key, token) = await keys.MintAsync("user-1", "CI", null, default);
        var secret = token.Split('.')[2];

        Assert.DoesNotContain(secret, key.SecretHash, StringComparison.Ordinal);
        Assert.Equal(AccessKeyToken.Hash(secret), key.SecretHash);

        var listed = await keys.ListAsync("user-1", default);
        Assert.DoesNotContain(listed, k => k.SecretHash.Contains(secret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_token_whose_secret_is_wrong_is_refused_even_with_the_right_id()
    {
        var (keys, _) = Store();

        var (key, _) = await keys.MintAsync("user-1", "CI", null, default);
        var forged = $"{AccessKeyToken.Prefix}.{key.Id}.not-the-secret";

        Assert.Null(await keys.VerifyAsync(forged, default));
    }

    [Theory]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("cordango_pat.only-two-parts")]
    [InlineData("wrong_prefix.abc.def")]
    [InlineData("cordango_pat..secret")]
    public async Task A_malformed_token_never_reaches_the_database(string token)
    {
        var (keys, _) = Store();

        Assert.Null(await keys.VerifyAsync(token, default));
        Assert.False(AccessKeyToken.TryParse(token, out _, out _));
    }

    [Fact]
    public async Task A_key_stops_working_the_moment_it_expires()
    {
        var (keys, clock) = Store();

        var (_, token) = await keys.MintAsync("user-1", "Temporary", Now.AddHours(1), default);
        Assert.Equal("user-1", await keys.VerifyAsync(token, default));

        clock.UtcNow = Now.AddHours(2);
        Assert.Null(await keys.VerifyAsync(token, default));
    }

    [Fact]
    public async Task An_expiry_in_the_past_is_refused_rather_than_stored()
    {
        var (keys, _) = Store();

        var refusal = await Assert.ThrowsAsync<RecordException>(
            () => keys.MintAsync("user-1", "Backdated", Now.AddDays(-1), default));

        Assert.Equal("key.expiry_in_past", refusal.Code);
    }

    [Fact]
    public async Task A_key_needs_a_name_somebody_will_recognise()
    {
        var (keys, _) = Store();

        var refusal = await Assert.ThrowsAsync<RecordException>(
            () => keys.MintAsync("user-1", "   ", null, default));

        Assert.Equal("key.label_required", refusal.Code);
    }

    [Fact]
    public async Task One_person_cannot_revoke_another_persons_key()
    {
        var (keys, _) = Store();

        var (key, token) = await keys.MintAsync("user-1", "CI", null, default);

        Assert.False(await keys.RevokeAsync("user-2", key.Id, default));
        Assert.Equal("user-1", await keys.VerifyAsync(token, default));

        Assert.True(await keys.RevokeAsync("user-1", key.Id, default));
        Assert.Null(await keys.VerifyAsync(token, default));
    }

    [Fact]
    public async Task Listing_shows_only_your_own_keys()
    {
        var (keys, _) = Store();

        await keys.MintAsync("user-1", "Mine", null, default);
        await keys.MintAsync("user-2", "Theirs", null, default);

        var mine = await keys.ListAsync("user-1", default);

        Assert.Single(mine);
        Assert.Equal("Mine", mine[0].Label);
    }

    [Fact]
    public async Task Last_used_is_recorded_but_not_rewritten_on_every_call()
    {
        var (keys, clock) = Store();

        var (key, token) = await keys.MintAsync("user-1", "CI", null, default);
        Assert.Null((await keys.ListAsync("user-1", default))[0].LastUsed);

        await keys.VerifyAsync(token, default);
        Assert.Equal(Now, (await keys.ListAsync("user-1", default))[0].LastUsed);

        // A second call moments later must not write again — an endpoint being polled would
        // otherwise turn every read into a write on this row.
        clock.UtcNow = Now.AddSeconds(10);
        await keys.VerifyAsync(token, default);
        Assert.Equal(Now, (await keys.ListAsync("user-1", default))[0].LastUsed);

        clock.UtcNow = Now.AddMinutes(5);
        await keys.VerifyAsync(token, default);
        Assert.Equal(Now.AddMinutes(5), (await keys.ListAsync("user-1", default))[0].LastUsed);

        Assert.Equal(key.Id, (await keys.ListAsync("user-1", default))[0].Id);
    }

    [Fact]
    public void Two_minted_tokens_never_collide()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < 500; i++)
        {
            var (id, secret, token) = AccessKeyToken.Mint();

            Assert.StartsWith(AccessKeyToken.Prefix + ".", token, StringComparison.Ordinal);
            Assert.True(seen.Add(id));
            Assert.True(seen.Add(secret));
        }
    }
}

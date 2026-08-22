// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Serialization;
using Cordango.Standalone.Data;
using Cordango.Standalone.Http;
using Cordango.Standalone.Records;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cordango.Standalone.Notifications;

/// <summary>
/// Something that happened, addressed to one person.
///
/// <para>In the application rather than in an inbox: a definition's <c>notify</c> effect means "tell
/// this person", and telling them inside the thing they are already using is the version that needs
/// no mail server, no deliverability and no unsubscribe link.</para>
/// </summary>
public sealed class Notification : IRecord
{
    public string Id { get; set; } = "";

    /// <summary>The directory Person this is for.</summary>
    [JsonPropertyName("person")] public string Person { get; set; } = "";

    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("message")] public string? Message { get; set; }

    /// <summary>Where to go when it is clicked — a record route, or nothing.</summary>
    [JsonPropertyName("link")] public string? Link { get; set; }

    [JsonPropertyName("created")] public DateTimeOffset Created { get; set; }
    [JsonPropertyName("read_at")] public DateTimeOffset? ReadAt { get; set; }
}

public static class NotificationModule
{
    public static ModelBuilder AddNotifications(this ModelBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Entity<Notification>(b =>
        {
            b.ToTable("notification");
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).HasColumnName("id").HasMaxLength(64);
            b.Property(e => e.Person).HasColumnName("person").HasMaxLength(64).IsRequired();
            b.Property(e => e.Title).HasColumnName("title").IsRequired();
            b.Property(e => e.Message).HasColumnName("message");
            b.Property(e => e.Link).HasColumnName("link");
            b.Property(e => e.Created).HasColumnName("created");
            b.Property(e => e.ReadAt).HasColumnName("read_at");

            // Every read of this table is "what is waiting for me", newest first.
            b.HasIndex(e => new { e.Person, e.Created });
        });
        return builder;
    }
}

/// <summary>Writing one. Separate from the controller because effects call it from inside a command,
/// where there is no request to read.</summary>
public sealed class NotificationService
{
    private readonly CordangoDbContext _db;
    private readonly IClock _clock;
    private readonly IRecordIdGenerator _ids;

    public NotificationService(CordangoDbContext db, IClock clock, IRecordIdGenerator ids)
    {
        _db = db;
        _clock = clock;
        _ids = ids;
    }

    /// <summary>
    /// Tell somebody. Silently does nothing when there is nobody to tell.
    ///
    /// <para>That silence is deliberate and narrow: a <c>notify</c> whose recipient field is empty —
    /// an unassigned ticket, a claim with no approver — is a normal state of the data, not an error
    /// in the application. Failing the command because of it would make the command unrunnable
    /// exactly when it is most needed.</para>
    /// </summary>
    public async Task<bool> SendAsync(string? person, string title, string? message, string? link, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(person) || string.IsNullOrWhiteSpace(title)) return false;

        _db.Add(new Notification
        {
            Id = _ids.NewId(),
            Person = person,
            Title = title,
            Message = message,
            Link = link,
            Created = _clock.UtcNow,
        });

        await _db.SaveChangesAsync(ct);
        return true;
    }
}

/// <summary>What is waiting for the person asking, and nobody else.</summary>
[Route("api/notifications")]
public sealed class NotificationsController : ControllerBase
{
    private readonly CordangoDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;

    public NotificationsController(CordangoDbContext db, ICurrentUser user, IClock clock)
    {
        _db = db;
        _user = user;
        _clock = clock;
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] bool unreadOnly = false, CancellationToken ct = default)
    {
        // Keyed by the signed-in person, so there is no id in the route to tamper with and no way to
        // phrase a request for somebody else's notifications.
        if (_user.PersonId is not { Length: > 0 } person)
            return Ok(new { items = Array.Empty<object>(), unread = 0 });

        var mine = _db.Set<Notification>().Where(n => n.Person == person);
        var unread = await mine.CountAsync(n => n.ReadAt == null, ct);

        var items = await (unreadOnly ? mine.Where(n => n.ReadAt == null) : mine)
            .OrderByDescending(n => n.Created)
            .Take(50)
            .ToListAsync(ct);

        return Ok(new { items, unread });
    }

    [HttpPost("{id}/read")]
    public async Task<IActionResult> Read(string id, CancellationToken ct)
    {
        if (_user.PersonId is not { Length: > 0 } person)
            return Unauthorized(this.Refuse("auth.required", "Sign in first."));

        // Scoped to the caller in the WHERE clause rather than checked afterwards: a notification
        // that is not theirs simply matches nothing.
        await _db.Set<Notification>()
            .Where(n => n.Id == id && n.Person == person)
            .ExecuteUpdateAsync(u => u.SetProperty(n => n.ReadAt, _clock.UtcNow), ct);

        return NoContent();
    }

    [HttpPost("read-all")]
    public async Task<IActionResult> ReadAll(CancellationToken ct)
    {
        if (_user.PersonId is not { Length: > 0 } person)
            return Unauthorized(this.Refuse("auth.required", "Sign in first."));

        await _db.Set<Notification>()
            .Where(n => n.Person == person && n.ReadAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(n => n.ReadAt, _clock.UtcNow), ct);

        return NoContent();
    }
}

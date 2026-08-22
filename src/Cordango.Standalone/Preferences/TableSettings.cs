// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.Standalone.Data;
using Cordango.Standalone.Http;
using Cordango.Standalone.Records;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;

namespace Cordango.Standalone.Preferences;

/// <summary>
/// One person's arrangement of one table: which columns, how wide, in what order, sorted how.
///
/// <para><b>Per person, and that is the point.</b> Column layout is a preference, not a
/// configuration: two people looking at the same list want different things from it, and making one
/// of them change it for everybody is how a shared view becomes an argument. The row is keyed by the
/// user, so nothing here is visible to or editable by anybody else.</para>
///
/// <para>The payload is stored as opaque JSON. What a table setting CONTAINS is the client's
/// business and changes as the table components do; the server's job is to keep it and hand it
/// back, which it can do without ever understanding it.</para>
/// </summary>
public sealed class TableSetting
{
    /// <summary>Composite: user, screen handle, table key.</summary>
    public string UserId { get; set; } = "";
    public string Handle { get; set; } = "";
    public string TableKey { get; set; } = "";

    public string Payload { get; set; } = "{}";

    public DateTimeOffset Updated { get; set; }
}

public static class TableSettingsModule
{
    /// <summary>Add the preferences table to the model. Called from the generated
    /// <c>DbContext</c>.</summary>
    public static ModelBuilder AddPreferences(this ModelBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Entity<TableSetting>(b =>
        {
            b.ToTable("user_table_settings");
            b.HasKey(e => new { e.UserId, e.Handle, e.TableKey });
            b.Property(e => e.UserId).HasColumnName("user_id").HasMaxLength(64);
            b.Property(e => e.Handle).HasColumnName("handle").HasMaxLength(120);
            b.Property(e => e.TableKey).HasColumnName("table_key").HasMaxLength(120);
            b.Property(e => e.Payload).HasColumnName("payload").HasColumnType("jsonb");
            b.Property(e => e.Updated).HasColumnName("updated");
        });
        return builder;
    }
}

/// <summary>
/// Reading and writing the caller's own table preferences, and only ever the caller's own.
///
/// <para>There is no id in any of these routes. The row is found by the signed-in user plus the
/// table being looked at, so there is no identifier to tamper with and no way to phrase a request
/// for somebody else's layout.</para>
/// </summary>
[Route("api/settings/table")]
public sealed class TableSettingsController : ControllerBase
{
    private readonly CordangoDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;

    public TableSettingsController(CordangoDbContext db, ICurrentUser user, IClock clock)
    {
        _db = db;
        _user = user;
        _clock = clock;
    }

    [HttpGet("{handle}/{tableKey}")]
    public async Task<IActionResult> Get(string handle, string tableKey, CancellationToken ct)
    {
        if (_user.UserId is not { } userId) return Unauthorized(this.Refuse("auth.required", "Sign in first."));

        var row = await _db.Set<TableSetting>()
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.UserId == userId && s.Handle == handle && s.TableKey == tableKey, ct);

        // Null rather than 404: having no saved layout is the ordinary state of every table the
        // first time somebody opens it, and a client should not have to treat "normal" as an error.
        return Ok(row is null ? null : System.Text.Json.JsonDocument.Parse(row.Payload).RootElement);
    }

    [HttpPut("{handle}/{tableKey}")]
    public async Task<IActionResult> Put(string handle, string tableKey, [FromBody] System.Text.Json.JsonElement payload, CancellationToken ct)
    {
        if (_user.UserId is not { } userId) return Unauthorized(this.Refuse("auth.required", "Sign in first."));

        var json = payload.GetRawText();
        if (json.Length > MaxPayload)
            throw new RecordException("settings.too_large", "That table layout is larger than the limit.", 413);

        var row = await _db.Set<TableSetting>()
            .FirstOrDefaultAsync(s => s.UserId == userId && s.Handle == handle && s.TableKey == tableKey, ct);

        if (row is null)
        {
            row = new TableSetting { UserId = userId, Handle = handle, TableKey = tableKey };
            _db.Add(row);
        }

        row.Payload = json;
        row.Updated = _clock.UtcNow;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("{handle}/{tableKey}")]
    public async Task<IActionResult> Delete(string handle, string tableKey, CancellationToken ct)
    {
        if (_user.UserId is not { } userId) return Unauthorized(this.Refuse("auth.required", "Sign in first."));

        await _db.Set<TableSetting>()
            .Where(s => s.UserId == userId && s.Handle == handle && s.TableKey == tableKey)
            .ExecuteDeleteAsync(ct);

        return NoContent();
    }

    /// <summary>A ceiling on a preference blob. Column widths do not need 64 KB, and without a limit
    /// this endpoint is a place for a signed-in caller to store whatever they like.</summary>
    private const int MaxPayload = 64 * 1024;
}

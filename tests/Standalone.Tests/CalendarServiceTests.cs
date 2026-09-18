// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json;
using System.Text.Json.Nodes;
using Cordango.Standalone.Calendar;
using Cordango.Standalone.Commands;
using Cordango.Standalone.Conditions;
using Cordango.Standalone.Data;
using Cordango.Standalone.Records;
using Cordango.Standalone.Security;

namespace Cordango.Standalone.Tests;

/// <summary>
/// One person's dates, from every entity that has any.
///
/// <para>The owner filter is the whole security story, so it is what most of this tests: it is set
/// here and can never arrive in a query string. An endpoint called "my calendar" that took an id
/// would be a way to read anybody's week, and the response would look entirely ordinary to whoever
/// reviewed it.</para>
/// </summary>
public class CalendarServiceTests
{
    [Fact]
    public async Task Only_this_persons_records_are_asked_for()
    {
        var gateway = Gateway("time_off", Row("1", "2026-09-10"));
        var window = await Read(Descriptor(TimeOff), gateway);

        Assert.Single(window.Entries);
        Assert.All(gateway.Asked,
            filters => Assert.Contains(filters,
                f => f.Field == "requested_by" && f.Operator == "eq" && f.Value == "anna"));
    }

    [Fact]
    public async Task An_entity_the_role_cannot_read_contributes_nothing()
    {
        var gateway = Gateway("time_off", Row("1", "2026-09-10")) with { Access = EntityAccess.None };

        Assert.Empty((await Read(Descriptor(TimeOff), gateway)).Entries);
        Assert.Empty(gateway.Asked);
    }

    /// <summary>A login that is not a directory person owns nothing. Not an error: an application can
    /// be administered by an account nobody put in the directory.</summary>
    [Fact]
    public async Task A_login_that_is_not_a_person_has_an_empty_calendar()
    {
        var gateway = Gateway("time_off", Row("1", "2026-09-10"));

        var window = await Read(Descriptor(TimeOff), gateway, personId: null);

        Assert.Empty(window.Entries);
        Assert.Empty(gateway.Asked);
    }

    /// <summary>A spanning entity takes two reads, because the overlap test is not one conjunction
    /// and the query layer has no OR. They partition on the start, so nothing is counted twice.</summary>
    [Fact]
    public async Task A_spanning_entity_is_read_as_two_windows_and_deduplicated()
    {
        var running = Row("1", "2026-08-20");
        running["end_date"] = "2026-09-15";

        var gateway = Gateway("time_off", running);
        var window = await Read(Descriptor(TimeOff), gateway);

        Assert.Equal(2, gateway.Asked.Count);
        Assert.Single(window.Entries);
        Assert.Equal("2026-09-15", window.Entries[0].End);
    }

    [Fact]
    public async Task A_point_entity_is_read_once()
    {
        var gateway = Gateway("milestone", Row("1", "2026-09-10"));

        await Read(Descriptor(PointOnly), gateway);

        Assert.Single(gateway.Asked);
    }

    /// <summary>The hop that puts a milestone in the calendar of whoever leads its project: the
    /// parents this person owns are read first, and each becomes its own filter on the child.</summary>
    [Fact]
    public async Task An_owned_entity_is_narrowed_through_the_parents_this_person_owns()
    {
        var parents = Gateway("project", Row("p1", "2026-09-01"), Row("p2", "2026-09-02"));
        var children = Gateway("milestone", Row("m1", "2026-09-10"));

        var window = await Read(Descriptor(Owned), children, parents);

        Assert.Single(window.Entries);
        Assert.All(parents.Asked, f => Assert.Contains(f, x => x.Field == "requested_by"));
        Assert.Equal(2, children.Asked.Count);
        Assert.Contains(children.Asked, f => f.Any(x => x.Field == "project" && x.Value == "p1"));
        Assert.Contains(children.Asked, f => f.Any(x => x.Field == "project" && x.Value == "p2"));
    }

    [Fact]
    public async Task A_hop_through_a_parent_the_role_cannot_read_contributes_nothing()
    {
        var parents = Gateway("project", Row("p1", "2026-09-01")) with { Access = EntityAccess.None };
        var children = Gateway("milestone", Row("m1", "2026-09-10"));

        Assert.Empty((await Read(Descriptor(Owned), children, parents)).Entries);
        Assert.Empty(children.Asked);
    }

    [Fact]
    public async Task A_hidden_record_stays_out()
    {
        var declined = Row("1", "2026-09-10");
        declined["status"] = "declined";

        var source = TimeOff with { HideWhen = Condition.Leaf("status", "eq", "declined") };
        var window = await Read(Descriptor(source), Gateway("time_off", declined, Row("2", "2026-09-11")));

        Assert.Single(window.Entries);
        Assert.Equal("2", window.Entries[0].RecordId);
    }

    [Fact]
    public async Task An_entry_takes_its_colour_and_its_state_from_the_option()
    {
        var approved = Row("1", "2026-09-10");
        approved["status"] = "approved";

        var window = await Read(Descriptor(TimeOff), Gateway("time_off", approved));

        Assert.Equal("#16a34a", window.Entries[0].Color);
        Assert.Equal("done", window.Entries[0].State?.Phase);
    }

    [Fact]
    public async Task A_title_template_reads_more_than_one_field()
    {
        var source = TimeOff with { Title = "{{reason}} — {{status}}" };
        var row = Row("1", "2026-09-10");
        row["status"] = "approved";

        var window = await Read(Descriptor(source), Gateway("time_off", row));

        Assert.Equal("Holiday — approved", window.Entries[0].Title);
    }

    [Fact]
    public async Task A_date_the_role_cannot_read_is_not_an_entry()
    {
        var blank = Row("1", "2026-09-10");
        blank["start_date"] = null;

        Assert.Empty((await Read(Descriptor(TimeOff), Gateway("time_off", blank))).Entries);
    }

    /// <summary>Refused, not trimmed. Nobody can tell an empty Tuesday from a Tuesday whose entries
    /// did not fit.</summary>
    [Fact]
    public async Task Too_many_entries_refuses_rather_than_truncating()
    {
        var rows = Enumerable.Range(0, CalendarService.MaxEntries + 2)
            .Select(i => Row(i.ToString(System.Globalization.CultureInfo.InvariantCulture), "2026-09-10"))
            .ToArray();

        var window = await Read(Descriptor(PointOnly), Gateway("milestone", rows));

        Assert.True(window.Truncated);
        Assert.Empty(window.Entries);
    }

    /// <summary>A page that does not hold the whole answer is the same failure one level down, and
    /// the quieter one: the read comes back, it just stops.</summary>
    [Fact]
    public async Task One_entity_holding_more_than_a_page_refuses_too()
    {
        var gateway = Gateway("milestone", Row("1", "2026-09-10")) with { Total = 4000 };

        var window = await Read(Descriptor(PointOnly), gateway);

        Assert.True(window.Truncated);
        Assert.Empty(window.Entries);
    }

    /// <summary>A field the role cannot read is not in the row, so its token survives substitution.
    /// Showing "Rollout — {{requested_by}}" reads as a broken page rather than as a hidden field.</summary>
    [Fact]
    public async Task A_token_for_a_field_the_role_cannot_read_is_removed_rather_than_shown()
    {
        var source = TimeOff with { Title = "{{reason}} — {{requested_by}}" };

        var window = await Read(Descriptor(source), Gateway("time_off", Row("1", "2026-09-10")));

        Assert.Equal("Holiday —", window.Entries[0].Title);
        Assert.DoesNotContain("{{", window.Entries[0].Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Entries_come_back_in_date_order()
    {
        var window = await Read(Descriptor(PointOnly), Gateway("milestone",
            Row("b", "2026-09-20"), Row("a", "2026-09-05"), Row("c", "2026-09-12")));

        Assert.Equal(["a", "c", "b"], window.Entries.Select(e => e.RecordId));
    }

    [Fact]
    public async Task An_end_a_role_cannot_move_is_not_offered_as_editable()
    {
        var source = TimeOff with { EndWritable = false };

        var window = await Read(Descriptor(source), Gateway("time_off", Row("1", "2026-09-10")));

        Assert.True(window.Entries[0].Editable.Start);
        Assert.False(window.Entries[0].Editable.End);
    }

    private static readonly CalendarSource TimeOff = new(
        Entity: "time_off",
        Start: "start_date",
        End: "end_date",
        Who: "requested_by",
        Via: null,
        Parent: null,
        Title: "reason",
        AllDay: true,
        StatusField: "status",
        Options:
        [
            new CalendarOption("pending", "Pending", "#f59e0b", "active"),
            new CalendarOption("approved", "Approved", "#16a34a", "done"),
        ],
        HideWhen: null,
        StartWritable: true,
        EndWritable: true);

    private static readonly CalendarSource PointOnly = TimeOff with
    {
        Entity = "milestone",
        End = null,
        StatusField = null,
        Options = [],
    };

    private static readonly CalendarSource Owned = PointOnly with
    {
        Via = "project",
        Parent = "project",
    };

    private static CalendarDescriptor Descriptor(CalendarSource source) => new([source]);

    private static JsonObject Row(string id, string start) => new()
    {
        ["id"] = id,
        ["start_date"] = start,
        ["reason"] = "Holiday",
    };

    private static FakeGateway Gateway(string entity, params JsonObject[] rows) =>
        new() { Entity = entity, Rows = rows };

    private static Task<CalendarWindow> Read(
        CalendarDescriptor descriptor, params FakeGateway[] gateways) =>
        Read(descriptor, "anna", gateways);

    private static Task<CalendarWindow> Read(
        CalendarDescriptor descriptor, FakeGateway gateway, string? personId) =>
        Read(descriptor, personId, [gateway]);

    private static Task<CalendarWindow> Read(
        CalendarDescriptor descriptor, string? personId, FakeGateway[] gateways) =>
        new CalendarService(descriptor, gateways, new FakeUser(personId))
            .ReadAsync("2026-09-01", "2026-10-01", CancellationToken.None);

    private sealed record FakeUser(string? PersonId) : ICurrentUser
    {
        public string? UserId => "login";
        public IReadOnlyCollection<string> RoleKeys => [];
        public bool IsAdministrator => false;
    }

    private sealed record FakeGateway : IRecordGateway
    {
        public required string Entity { get; init; }
        public required IReadOnlyList<JsonObject> Rows { get; init; }

        /// <summary>What the store says it HOLDS, which a page of 500 need not be all of.</summary>
        public int? Total { get; init; }
        public EntityAccess Access { get; init; } = EntityAccess.Full;

        /// <summary>Every filter set this gateway was asked for, so a test can assert what the
        /// service NARROWED by rather than only what came back.</summary>
        public List<IReadOnlyList<RecordFilter>> Asked { get; } = [];

        public string Label => Entity;
        public IReadOnlyList<string> FieldKeys => [];

        public Task<JsonObject> ListAsync(
            IReadOnlyList<RecordFilter> filters, IReadOnlyList<RecordSort> sort,
            int skip, int take, CancellationToken ct)
        {
            Asked.Add(filters);
            return Task.FromResult(new JsonObject
            {
                ["items"] = new JsonArray([.. Rows.Select(r => (JsonNode)r.DeepClone())]),
                ["total"] = Total ?? Rows.Count,
            });
        }

        public Task<JsonObject> AggregateAsync(
            string op, string? field, string? groupBy, IReadOnlyList<RecordFilter> filters, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<JsonObject> GetAsync(string id, CancellationToken ct) => throw new NotSupportedException();

        public Task<JsonObject> CreateAsync(JsonElement body, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<JsonObject> WriteAsync(
            string id, JsonElement body, IReadOnlyList<string> fields, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string id, CancellationToken ct) => throw new NotSupportedException();

        public Task<CommandResult> RunCommandAsync(
            string id, string command, JsonElement input, CancellationToken ct) =>
            throw new NotSupportedException();

        public IReadOnlyList<string> SuppliedKeys(JsonElement body) => [];
    }
}

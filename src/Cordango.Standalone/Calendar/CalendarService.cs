// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cordango.Standalone.Conditions;
using Cordango.Standalone.Data;
using Cordango.Standalone.Records;

namespace Cordango.Standalone.Calendar;

/// <summary>The state an entry is in: what a person reads, and the phase that decides how it is
/// DRAWN. A pending request and an approved one have to be distinguishable without reading.</summary>
public sealed record CalendarEntryState(string Key, string Label, string? Phase);

/// <summary>Which ends of an entry this caller could move. Asked per entry rather than per entity
/// because the two ends are separate fields and a role can lock one of them.</summary>
public sealed record CalendarEditable(bool Start, bool End);

/// <param name="Id">Stable across a reload: entity and record, which is what a grid keys on.</param>
public sealed record CalendarEntry(
    string Id,
    string Entity,
    string EntityLabel,
    string RecordId,
    string Title,
    string Start,
    string? End,
    bool AllDay,
    string? Color,
    CalendarEntryState? State,
    string Link,
    CalendarEditable Editable);

/// <summary>What a window read returned. <see cref="Truncated"/> is never true in a successful read —
/// the service refuses instead — and exists so a caller can say WHY it refused.</summary>
public sealed record CalendarWindow(IReadOnlyList<CalendarEntry> Entries, bool Truncated, int Total);

/// <summary>
/// One person's dates, across every entity of this application that has any, in one window.
///
/// <para><b>Server-side, and the owner filter is why.</b> The filter naming the person is set here
/// and can never arrive in a query string. A caller who could name the person would be reading
/// somebody else's calendar, and on an endpoint called "my calendar" that does not look like a leak
/// to anybody reviewing the response. The same reasoning as the platform's union service, minus the
/// part that spans apps — there is one app here.</para>
///
/// <para><b>Read through <see cref="IRecordGateway"/>, not the store.</b> The owner filter expresses
/// "records ABOUT me" and the gateway expresses "records I MAY SEE", and they are different
/// questions. Going through the gateway is what keeps the calendar from becoming a way to read an
/// entity the caller's role does not grant — and it applies field visibility too, so a title built
/// from a field somebody may not read comes back empty rather than leaked.</para>
/// </summary>
public sealed class CalendarService
{
    /// <summary>
    /// Above this the read REFUSES rather than returning a page.
    ///
    /// <para>A truncated list is a short list; a truncated calendar is a meeting that silently is not
    /// there. Nobody can tell an empty Tuesday from a Tuesday whose entries did not fit.</para>
    /// </summary>
    public const int MaxEntries = 2000;

    /// <summary>How many rows one entity is read for per query. The window has already narrowed this
    /// to a month or so of one person's records; the cap is what stops a pathological entity from
    /// being read whole before <see cref="MaxEntries"/> can refuse it.</summary>
    private const int PageSize = 500;

    /// <summary>How many parents a hop will follow. Beyond this the entity contributes nothing, which
    /// is the honest answer: silently following the first two hundred is a calendar that is wrong
    /// without saying so, and a person who genuinely leads more projects than this has a calendar
    /// nobody could read anyway.</summary>
    private const int MaxHopParents = 200;

    private readonly CalendarDescriptor _calendar;
    private readonly IEnumerable<IRecordGateway> _gateways;
    private readonly ICurrentUser _user;

    public CalendarService(
        CalendarDescriptor calendar, IEnumerable<IRecordGateway> gateways, ICurrentUser user)
    {
        _calendar = calendar;
        _gateways = gateways;
        _user = user;
    }

    /// <summary>Every entry belonging to the signed-in person that touches [from, until).</summary>
    /// <param name="from">Inclusive lower bound, ISO.</param>
    /// <param name="until">EXCLUSIVE upper bound, ISO. Exclusive so a caller can ask for a month by
    /// naming the first of the next one and never has to know whether the field carries a time.</param>
    public async Task<CalendarWindow> ReadAsync(string from, string until, CancellationToken ct)
    {
        // A login that is not a person has no calendar. Not an error: an application can be
        // administered by an account nobody put in the directory, and that account simply owns
        // nothing.
        if (_user.PersonId is not { Length: > 0 } me) return new CalendarWindow([], false, 0);

        var now = DateTimeOffset.UtcNow;
        var entries = new List<CalendarEntry>();

        foreach (var source in _calendar.Sources)
        {
            if (Gateway(source.Entity) is not { } gateway || !gateway.Access.Read) continue;

            List<JsonObject> rows;
            try
            {
                rows = await RowsAsync(source, me, from, until, ct);
            }
            catch (TooManyException tooMany)
            {
                return new CalendarWindow([], true, tooMany.Total);
            }

            foreach (var row in rows)
            {
                // `is { } hide &&` is load-bearing. Evaluate(null, ...) answers TRUE — "no condition"
                // holds, which is right for a command guard and exactly wrong for a hide rule. Passing
                // the null straight through hid every record of every entity that had no hideWhen,
                // which is to say almost all of them, and the calendar came back empty with nothing
                // anywhere saying why.
                if (source.HideWhen is { } hide && ConditionEvaluator.Evaluate(hide, row, me, now))
                    continue;
                if (Entry(source, gateway, row) is not { } entry) continue;

                entries.Add(entry);
                if (entries.Count > MaxEntries) return new CalendarWindow([], true, entries.Count);
            }
        }

        entries.Sort(static (a, b) => string.CompareOrdinal(a.Start, b.Start) is var by && by != 0
            ? by
            : string.CompareOrdinal(a.Id, b.Id));

        return new CalendarWindow(entries, false, entries.Count);
    }

    private IRecordGateway? Gateway(string entity) =>
        _gateways.FirstOrDefault(g => string.Equals(g.Entity, entity, StringComparison.Ordinal));

    /// <summary>
    /// Every row of one entity that belongs to this person and touches this window.
    ///
    /// <para><b>A window over a spanning entity takes two reads.</b> The overlap test is
    /// <c>start &lt; until AND (end ?? start) &gt;= from</c>; the query layer ANDs its predicates and
    /// has no OR, and no rearranging makes that one conjunction. It splits cleanly on the start into
    /// "starts inside the window" and "started earlier, still running", a row cannot satisfy both,
    /// and a row with no end is correctly in the first and correctly out of the second.</para>
    /// </summary>
    private async Task<List<JsonObject>> RowsAsync(
        CalendarSource source, string me, string from, string until, CancellationToken ct)
    {
        // Not a collection expression inside a list initialiser: `{ [a, b] }` parses as an INDEXER
        // assignment, not as a nested list, and the error it gives says nothing about that.
        var windows = new List<RecordFilter[]>();
        windows.Add([new(source.Start, "gte", from), new(source.Start, "lt", until)]);

        if (source.End is { } end)
            windows.Add([new(source.Start, "lt", from), new(end, "gte", from)]);

        var owners = await OwnersAsync(source, me, ct);
        if (owners.Count == 0) return [];

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<JsonObject>();

        foreach (var owner in owners)
            foreach (var window in windows)
                foreach (var row in await PageAsync(source.Entity, [owner, .. window], ct))
                    if (Str(row, "id") is { } id && seen.Add(id)) rows.Add(row);

        return rows;
    }

    /// <summary>
    /// The equality filter (or filters) narrowing an entity to THIS person's rows.
    ///
    /// <para>One filter for the common case. For a hop — an entity with no person of its own, owned
    /// by one that has — the parents this person owns are read first and each becomes its own
    /// filter.</para>
    /// </summary>
    private async Task<List<RecordFilter>> OwnersAsync(
        CalendarSource source, string me, CancellationToken ct)
    {
        if (source.Via is not { } via || source.Parent is not { } parent)
            return [new RecordFilter(source.Who, "eq", me)];

        // The parent is read through its OWN gateway, so a person who may not read projects gets no
        // milestones through the back door of a calendar.
        if (Gateway(parent) is not { } gateway || !gateway.Access.Read) return [];

        var parents = await PageAsync(parent, [new RecordFilter(source.Who, "eq", me)], ct);
        if (parents.Count > MaxHopParents) return [];

        return [.. parents
            .Select(p => Str(p, "id"))
            .Where(id => id is { Length: > 0 })
            .Select(id => new RecordFilter(via, "eq", id))];
    }

    /// <summary>
    /// One narrowed read, through the gateway every other surface uses — so the window becomes real
    /// predicates rather than a filter over a page.
    ///
    /// <para>A read that would need a SECOND page throws rather than returning the first. Taking the
    /// first 500 would be the truncation this service exists to refuse, and it would be invisible:
    /// the caller gets a calendar, it just quietly stops. <see cref="TooManyException"/> is caught
    /// where the refusal is assembled.</para>
    /// </summary>
    private async Task<List<JsonObject>> PageAsync(
        string entity, IReadOnlyList<RecordFilter> filters, CancellationToken ct)
    {
        if (Gateway(entity) is not { } gateway) return [];

        var page = await gateway.ListAsync(filters, [], 0, PageSize, ct);

        if (page["total"] is JsonValue total && total.TryGetValue<int>(out var count) && count > PageSize)
            throw new TooManyException(count);

        return page["items"] is JsonArray items ? [.. items.OfType<JsonObject>()] : [];
    }

    /// <summary>One entity alone held more than a page of this person's records in this window.</summary>
    private sealed class TooManyException(int total) : Exception
    {
        public int Total { get; } = total;
    }

    private static CalendarEntry? Entry(CalendarSource source, IRecordGateway gateway, JsonObject row)
    {
        if (Str(row, "id") is not { } id) return null;

        // The window matched on this field, so it is set — unless the caller's role cannot read it,
        // in which case an entry with no date is not an entry.
        if (Str(row, source.Start) is not { Length: > 0 } start) return null;

        var option = source.StatusField is { } status
            ? source.Options.FirstOrDefault(o => o.Value == Str(row, status))
            : null;

        return new CalendarEntry(
            Id: $"{source.Entity}/{id}",
            Entity: source.Entity,
            EntityLabel: gateway.Label,
            RecordId: id,
            Title: Title(source, row),
            Start: start,
            End: source.End is { } end ? Str(row, end) : null,
            AllDay: source.AllDay,
            Color: option?.Color,
            State: option is null ? null : new CalendarEntryState(option.Value, option.Label, option.Phase),
            Link: $"/record/{source.Entity}/{Uri.EscapeDataString(id)}",
            Editable: new CalendarEditable(
                gateway.Access.Update && source.StartWritable && gateway.Access.CanUpdateField(source.Start),
                source.End is { } editable
                    && gateway.Access.Update
                    && source.EndWritable
                    && gateway.Access.CanUpdateField(editable)));
    }

    /// <summary>The entry's text: a template over the row's fields, or one field key. Templates are
    /// tried first, because a title reading "Rollout — Anna" needs two fields and a display field is
    /// only ever one.</summary>
    private static string Title(CalendarSource source, JsonObject row)
    {
        if (!source.Title.Contains("{{", StringComparison.Ordinal))
            return Str(row, source.Title) ?? "";

        var text = source.Title;
        foreach (var (key, _) in row)
            text = text.Replace("{{" + key + "}}", Str(row, key) ?? "", StringComparison.Ordinal);

        // A field the caller's role may not read is not IN the row, so its token is still here. It
        // has to go: a title reading "Rollout — {{owner}}" shows the template to somebody who was
        // never going to see the value, which reads as a broken page rather than as a hidden field.
        return Tokens.Replace(text, "").Trim();
    }

    /// <summary>Whatever is left of a title template once every readable field has been filled in.</summary>
    private static readonly Regex Tokens = new(@"\{\{[^}]*\}\}", RegexOptions.Compiled);

    private static string? Str(JsonObject row, string key) => row[key] switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        var node => node.ToString(),
    };
}

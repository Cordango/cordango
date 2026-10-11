// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json;
using System.Text.Json.Nodes;
using Cordango.Standalone.Commands;
using Cordango.Standalone.Http;
using Cordango.Standalone.Records;
using Cordango.Standalone.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cordango.Standalone.Data;

/// <summary>
/// Every operation this application offers on one entity, with the caller's permissions applied, and
/// without knowing the entity's type.
///
/// <para><b>Why an untyped façade, when <see cref="IEntityWriter"/> already is one.</b> That one is
/// for workflows, which run as the application rather than as a person and deliberately go around
/// the permission layer. This one is the opposite: it exists so that a caller who is NOT a browser —
/// an MCP client, a script, anything holding a credential — reaches exactly what the same person
/// reaches through the UI, resolved through the same <see cref="EntityAccess"/> and projected
/// through the same <see cref="RecordVisibility"/>.</para>
///
/// <para><b>And why the controller uses it too.</b> An MCP tool and a REST route that each
/// implemented "list, with permissions" would be two implementations of one rule, and they would
/// disagree the first time somebody fixed a bug in one of them. So this is the implementation and
/// <c>RecordsController&lt;T&gt;</c> is an HTTP skin over it: the routes, the status codes and the
/// query-string parsing are the controller's, everything a caller may see or do is here.</para>
///
/// <para>Refusals are thrown as <see cref="RecordException"/>, which is what the error middleware
/// already turns into the <c>{code, error}</c> wire in the caller's language — so a refusal reads
/// identically whichever face asked.</para>
/// </summary>
public interface IRecordGateway
{
    /// <summary>The entity key as the definition spells it.</summary>
    string Entity { get; }

    /// <summary>The human label, for a message about this entity.</summary>
    string Label { get; }

    /// <summary>What this caller may do here. Resolved once per request.</summary>
    EntityAccess Access { get; }

    /// <summary>Every field key this entity has, for a caller checking a payload before sending it.</summary>
    IReadOnlyList<string> FieldKeys { get; }

    Task<JsonObject> ListAsync(
        IReadOnlyList<RecordFilter> filters, IReadOnlyList<RecordSort> sort, int skip, int take, CancellationToken ct);

    Task<JsonObject> AggregateAsync(
        string op, string? field, string? groupBy, IReadOnlyList<RecordFilter> filters, CancellationToken ct);

    /// <summary>
    /// The ids of every row ONE filter on this entity matches, as this caller may see them.
    ///
    /// <para>The other half of a filter that follows a reference: an order line asking for
    /// <c>item.barcode eq 4006381333931</c> asks the parts which of them carry that barcode, and the
    /// parts answer under their OWN read rules — so the filter cannot become a way to probe a field,
    /// or an entity, the caller could not have read directly. Every row, not a page: a page would
    /// quietly drop the matches past it.</para>
    ///
    /// <para>Refused by default, for a gateway that has no rows of its own to search.</para>
    /// </summary>
    Task<IReadOnlyList<string>> MatchingIdsAsync(RecordFilter filter, CancellationToken ct) =>
        throw new RecordException("query.path_invalid", $"A filter cannot follow a reference into {Label}.");

    Task<JsonObject> GetAsync(string id, CancellationToken ct);

    Task<JsonObject> CreateAsync(JsonElement body, CancellationToken ct);

    /// <summary>Write the fields named by <paramref name="fields"/>. Pass
    /// <see cref="FieldKeys"/> for a replace and the body's own keys for a patch — the difference
    /// between the two verbs is entirely in this argument.</summary>
    Task<JsonObject> WriteAsync(
        string id, JsonElement body, IReadOnlyList<string> fields, CancellationToken ct);

    Task DeleteAsync(string id, CancellationToken ct);

    Task<CommandResult> RunCommandAsync(string id, string command, JsonElement input, CancellationToken ct);

    /// <summary>The keys a body actually named. Needed because "absent" and "explicitly null" are
    /// different requests and deserialisation cannot tell them apart.</summary>
    IReadOnlyList<string> SuppliedKeys(JsonElement body);
}

/// <summary>The one implementation, generic over the entity. Registered per entity by
/// <c>AddRecord</c> and resolvable as a collection, so a caller naming an entity with a STRING can
/// find the right one without reflection or a service locator keyed by <see cref="Type"/>.</summary>
public class RecordGateway<T> : IRecordGateway where T : class, IRecord, new()
{
    /// <summary>A ceiling on <c>take</c>, so one request cannot ask for the whole table.</summary>
    public const int MaxPageSize = 500;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IRecordStore<T> _store;
    private readonly AppPermissions _permissions;
    private readonly CommandService<T> _commands;
    private readonly IServiceProvider? _services;

    private EntityAccess? _access;

    /// <param name="services">Where the OTHER entities' gateways are found, for a filter that
    /// follows a reference. Optional, and resolved at the moment a filter needs one rather than
    /// injected as a collection: the collection holds this gateway too, and asking for it while this
    /// one is being built is a cycle. Without it a filter naming <c>item.barcode</c> is refused by
    /// name.</param>
    public RecordGateway(
        IRecordStore<T> store, AppPermissions permissions, ICurrentUser user, CommandService<T> commands,
        IServiceProvider? services = null)
    {
        _store = store;
        _permissions = permissions;
        _commands = commands;
        _services = services;
        Caller = user;
    }

    /// <summary>Who is asking. Named <c>Caller</c> rather than <c>User</c> because a controller's
    /// <c>User</c> already means the raw <c>ClaimsPrincipal</c>, and two spellings of "the user"
    /// differing in what they let you check is exactly the pair somebody gets wrong at three in the
    /// morning.</summary>
    protected ICurrentUser Caller { get; }

    /// <summary>This entity's own descriptor, for a subclass that needs it.</summary>
    protected RecordDescriptor<T> Descriptor => _store.Descriptor;

    public string Entity => _store.Descriptor.EntityKey;

    public string Label => _store.Descriptor.Label;

    public IReadOnlyList<string> FieldKeys => _store.Descriptor.FieldKeys;

    /// <summary>Resolved once, and cached: it reads compiled-in data and touches nothing else, but
    /// a request that lists and then writes should not ask twice and risk two answers.</summary>
    public EntityAccess Access => _access ??= ResolveAccess();

    /// <summary>
    /// What this caller may do here — the definition's roles, for an application's own entities.
    ///
    /// <para>Overridable because not everything a generated application serves came from the
    /// definition. The built-in directory did not, and no role in the definition says anything about
    /// it; see <see cref="Directory.DirectoryGateway{T}"/>. Overriding HERE rather than on the
    /// controller is what makes the answer the same over HTTP and over MCP.</para>
    /// </summary>
    protected virtual EntityAccess ResolveAccess() =>
        PermissionResolver.Resolve(_permissions, Caller, Entity);

    public async Task<JsonObject> ListAsync(
        IReadOnlyList<RecordFilter> filters, IReadOnlyList<RecordSort> sort, int skip, int take, CancellationToken ct)
    {
        Require(Access.Read, "read");

        take = Math.Clamp(take, 1, MaxPageSize);
        skip = Math.Max(skip, 0);

        var (followed, own) = await FollowAsync(_store.Query(), filters ?? [], ct);
        var query = RecordQuery.Apply(followed, _store.Descriptor, own, sort ?? []);

        // Counted before paging, so a caller can say "31 of 214" rather than "31 of the 31 you can
        // see".
        var total = await query.CountAsync(ct);
        var rows = await query.Skip(skip).Take(take).ToListAsync(ct);

        var items = new JsonArray();
        foreach (var row in rows) items.Add(RecordVisibility.Project(Access, row, Json));

        return new JsonObject
        {
            ["items"] = items,
            ["total"] = total,
            ["skip"] = skip,
            ["take"] = take,
        };
    }

    public async Task<JsonObject> AggregateAsync(
        string op, string? field, string? groupBy, IReadOnlyList<RecordFilter> filters, CancellationToken ct)
    {
        Require(Access.Read, "read");

        // A field this role cannot read cannot be summed either. Without this, a total over salary
        // would tell somebody the payroll of a column they may not see — an aggregate is a slower
        // way of reading a field, not a different kind of access.
        if (field is not null && !Access.CanReadField(field))
            throw RecordException.Forbidden("record.read_denied", $"Your role may not read {field}.");

        if (groupBy is not null)
        {
            var grouped = groupBy.StartsWith("month_of:", StringComparison.Ordinal)
                ? groupBy["month_of:".Length..]
                : groupBy;

            if (!Access.CanReadField(grouped))
                throw RecordException.Forbidden("record.read_denied", $"Your role may not read {grouped}.");
        }

        var (followed, own) = await FollowAsync(_store.Query(), filters ?? [], ct);
        var query = RecordQuery.Narrow(followed, _store.Descriptor, own);
        var buckets = await RecordAggregate.RunAsync(query, _store.Descriptor, op, field, groupBy, ct);

        var items = new JsonArray();
        foreach (var bucket in buckets)
            items.Add(new JsonObject { ["key"] = bucket.Key, ["value"] = bucket.Value });

        return new JsonObject { ["op"] = op, ["buckets"] = items };
    }

    public async Task<IReadOnlyList<string>> MatchingIdsAsync(RecordFilter filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        Require(Access.Read, "read");

        if (!Access.CanReadField(filter.Field))
            throw RecordException.Forbidden("record.read_denied", $"Your role may not read {filter.Field}.");

        return await RecordQuery.Narrow(_store.Query(), _store.Descriptor, [filter])
            .Select(e => e.Id)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Narrow by every filter that reads a field ONE REFERENCE AWAY, and hand back the rest.
    ///
    /// <para><c>item.barcode</c> is a question for the parts, so the parts are asked it — through
    /// their own gateway, under their own read rules — and the lines are narrowed to the ones pointing
    /// at whatever matched. The reference itself is read on THIS side, so it is checked here: a role
    /// that may not see which part a line is for may not filter lines by it either.</para>
    ///
    /// <para>One hop. The target is asked with a plain field, and a second dot there is refused by its
    /// own allowlist as a field it does not have.</para>
    /// </summary>
    private async Task<(IQueryable<T> Query, IReadOnlyList<RecordFilter> Own)> FollowAsync(
        IQueryable<T> query, IReadOnlyList<RecordFilter> filters, CancellationToken ct)
    {
        var own = new List<RecordFilter>();

        foreach (var filter in filters)
        {
            var dot = filter.Field.IndexOf('.', StringComparison.Ordinal);
            if (dot < 0)
            {
                own.Add(filter);
                continue;
            }

            var via = filter.Field[..dot];

            if (!_store.Descriptor.TryGetField(via, out var reference) || reference.Target is not { } target)
                throw new RecordException("query.path_invalid",
                    $"'{Entity}.{via}' is not a reference a filter can follow.");

            if (!Access.CanReadField(via))
                throw RecordException.Forbidden("record.read_denied", $"Your role may not read {via}.");

            var gateway = _services?.GetServices<IRecordGateway>()
                    .FirstOrDefault(g => string.Equals(g.Entity, target, StringComparison.Ordinal))
                ?? throw new RecordException("query.path_invalid",
                    $"'{Entity}.{via}' points at '{target}', which this application does not serve.");

            var ids = await gateway.MatchingIdsAsync(filter with { Field = filter.Field[(dot + 1)..] }, ct);
            query = RecordQuery.Within(query, _store.Descriptor, via, ids);
        }

        return (query, own);
    }

    public async Task<JsonObject> GetAsync(string id, CancellationToken ct)
    {
        Require(Access.Read, "read");

        var record = await _store.FindAsync(id, ct) ?? throw RecordException.NotFound(Entity, id);
        return Projected(record);
    }

    public async Task<JsonObject> CreateAsync(JsonElement body, CancellationToken ct)
    {
        Require(Access.Create, "create");

        var (record, supplied) = Read(body);
        Refuse(supplied);

        return Projected(await _store.CreateAsync(record, ct));
    }

    public async Task<JsonObject> WriteAsync(
        string id, JsonElement body, IReadOnlyList<string> fields, CancellationToken ct)
    {
        Require(Access.Update, "update");

        var (record, supplied) = Read(body);

        // Checked against what the CALLER SENT, not against the field list being written. A replace
        // writes every field by definition, and refusing it because the payload happened to include
        // one the role may not set is right; refusing it because the entity HAS such a field would
        // make replace impossible for that role rather than merely restricted.
        Refuse(supplied);

        return Projected(await _store.UpdateAsync(id, record, fields, ct));
    }

    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        Require(Access.Delete, "delete");
        await _store.DeleteAsync(id, ct);
    }

    /// <summary>A command is not an update with a different name, so it is not guarded here: the
    /// permission, the legality of the state transition and the required input are all checked by
    /// <see cref="CommandService{T}"/>, in that order and for the reason documented there.</summary>
    public Task<CommandResult> RunCommandAsync(string id, string command, JsonElement input, CancellationToken ct) =>
        _commands.RunAsync(id, command, input, Access, ct);

    /// <summary>One row, with the fields this role may not read removed. <c>Project</c> answers null
    /// only for a null record, and every caller here has already established there is one — but an
    /// empty object is a better answer than a crash if that ever stops being true.</summary>
    private JsonObject Projected(T record) => RecordVisibility.Project(Access, record, Json) ?? [];

    public IReadOnlyList<string> SuppliedKeys(JsonElement body) =>
        body.ValueKind == JsonValueKind.Object
            ? [.. body.EnumerateObject().Select(p => p.Name)]
            : [];

    /// <summary>403 rather than 404. Hiding an entity's existence from somebody who already knows its
    /// route buys nothing here — every route is in the application's own generated client, and in its
    /// OpenAPI document.</summary>
    private void Require(bool allowed, string operation)
    {
        if (allowed) return;

        throw RecordException.Forbidden(
            $"record.{operation}_denied", $"Your role may not {operation} {Label}.");
    }

    private (T Record, IReadOnlyList<string> Supplied) Read(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
            throw new RecordException("request.body_invalid", "Expected a JSON object.");

        T record;
        try
        {
            record = body.Deserialize<T>(Json) ?? new T();
        }
        catch (JsonException ex)
        {
            // The message names the offending property and says what type was expected, which is the
            // one piece of exception text worth putting on the wire.
            throw new RecordException("request.body_invalid", ex.Message);
        }

        return (record, SuppliedKeys(body));
    }

    /// <summary>Refuse a write that touches fields this role may not set — all of them at once, so a
    /// form marks every offending field rather than one per attempt.</summary>
    private void Refuse(IReadOnlyList<string> supplied)
    {
        var rejected = RecordVisibility.RejectedWrites(Access, supplied);
        if (rejected.Count > 0) throw RecordException.WriteRestricted(rejected);
    }
}

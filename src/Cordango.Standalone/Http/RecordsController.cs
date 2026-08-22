// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json;
using System.Text.Json.Nodes;
using Cordango.Standalone.Data;
using Cordango.Standalone.Records;
using Cordango.Standalone.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cordango.Standalone.Http;

/// <summary>
/// CRUD over one entity, with the definition's permissions enforced on every path.
///
/// <para><b>How a generated application uses it.</b> One derived class per entity, and the class is
/// all there is to it:</para>
///
/// <code>
/// [Route("api/expense")]
/// public sealed class ExpenseController : RecordsController&lt;Expense&gt;
/// {
///     public ExpenseController(IRecordStore&lt;Expense&gt; store, AppPermissions permissions, ICurrentUser user)
///         : base(store, permissions, user) { }
/// }
/// </code>
///
/// <para>The framework this pattern came from conjured that class at startup instead — a feature
/// provider closing the generic over every discovered type, plus a route convention rewriting the
/// route from an attribute. It is clever, and for a library meeting its entities at runtime it is
/// the only option. We meet ours at build time, so we write the six lines. What that buys is a route
/// table you can read, a controller you can put a breakpoint in, and a place for the first
/// hand-written endpoint to go that does not require understanding the conjuring first.</para>
/// </summary>
public abstract class RecordsController<T> : ControllerBase where T : class, IRecord, new()
{
    private readonly IRecordStore<T> _store;
    private readonly AppPermissions _permissions;

    protected RecordsController(IRecordStore<T> store, AppPermissions permissions, ICurrentUser user)
    {
        _store = store;
        _permissions = permissions;
        Caller = user;
    }

    /// <summary>Who is asking. Named <c>Caller</c> rather than <c>User</c> because
    /// <see cref="ControllerBase.User"/> already means the raw <c>ClaimsPrincipal</c>, and two
    /// spellings of "the user" differing in what they let you check is exactly the pair somebody
    /// gets wrong at three in the morning.</summary>
    protected ICurrentUser Caller { get; }

    private EntityAccess? _access;

    /// <summary>Resolved once per request. Cheap — it reads compiled-in data and touches nothing
    /// else.</summary>
    protected EntityAccess Access => _access ??= ResolveAccess();

    /// <summary>
    /// What this caller may do here. The definition's roles, for an application's own entities.
    ///
    /// <para>Overridable because not everything a generated application serves came from the
    /// definition — the built-in directory did not, and no role in the definition says anything
    /// about it. See <see cref="Directory.DirectoryController{T}"/>.</para>
    /// </summary>
    protected virtual EntityAccess ResolveAccess() =>
        PermissionResolver.Resolve(_permissions, Caller, _store.Descriptor.EntityKey);

    protected RecordDescriptor<T> Descriptor => _store.Descriptor;

    protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// A page of records, narrowed and ordered by the request.
    ///
    /// <para><c>filter</c> may be repeated: <c>?filter=status:eq:open&amp;filter=amount:gt:100</c>.
    /// <c>sort</c> is a comma-separated list where a leading minus means descending:
    /// <c>?sort=-spent_on,description</c>. Both name the definition's own field keys, and a key this
    /// entity does not have is refused by name rather than ignored — a filter that silently did
    /// nothing would show a list that looks filtered and is not.</para>
    ///
    /// <para>The id is always the last sort term, including when the caller gave none. Paging is
    /// only meaningful over a total order: order by a status half the rows share and the database
    /// may return those rows differently on each page, so one row appears twice and another never
    /// appears at all.</para>
    /// </summary>
    [HttpGet]
    public virtual async Task<IActionResult> List(
        [FromQuery(Name = "filter")] string[]? filter = null,
        [FromQuery] string? sort = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        if (!Access.Read) return Denied("read");

        take = Math.Clamp(take, 1, MaxPageSize);
        skip = Math.Max(skip, 0);

        var query = RecordQuery.Apply(
            _store.Query(),
            Descriptor,
            RecordQuery.ParseFilters(filter),
            RecordQuery.ParseSort(sort));

        // Counted before paging, so the client can show "31 of 214" rather than "31 of the 31 you
        // can see".
        var total = await query.CountAsync(ct);
        var rows = await query.Skip(skip).Take(take).ToListAsync(ct);

        var items = new JsonArray();
        foreach (var row in rows) items.Add(RecordVisibility.Project(Access, row, Json));

        return Ok(new JsonObject
        {
            ["items"] = items,
            ["total"] = total,
            ["skip"] = skip,
            ["take"] = take,
        });
    }

    /// <summary>
    /// One figure, or one series of them: <c>?op=sum&amp;field=amount&amp;groupBy=category</c>.
    ///
    /// <para>Computed by the database. A stat card asking for a total must not make the browser
    /// download every row to add them up, and an overview page has four of these on it.</para>
    ///
    /// <para><c>groupBy</c> also accepts <c>month_of:&lt;field&gt;</c>, because "spend per month" is
    /// the question a chart asks and "spend per day, added up by the reader" is not.</para>
    /// </summary>
    [HttpGet("aggregate")]
    public virtual async Task<IActionResult> Aggregate(
        [FromQuery] string op = "count",
        [FromQuery] string? field = null,
        [FromQuery] string? groupBy = null,
        [FromQuery(Name = "filter")] string[]? filter = null,
        CancellationToken ct = default)
    {
        if (!Access.Read) return Denied("read");

        // A field this role cannot read cannot be summed either. Without this, a total over salary
        // would tell somebody the payroll of a column they are not allowed to see — an aggregate is
        // a slower way to read a field, not a different kind of access.
        if (field is not null && !Access.CanReadField(field))
            return StatusCode(403, this.Refuse("record.read_denied", $"Your role may not read {field}.", [field]));

        if (groupBy is not null)
        {
            var grouped = groupBy.StartsWith("month_of:", StringComparison.Ordinal)
                ? groupBy["month_of:".Length..]
                : groupBy;

            if (!Access.CanReadField(grouped))
                return StatusCode(403, this.Refuse("record.read_denied", $"Your role may not read {grouped}.", [grouped]));
        }

        var query = RecordQuery.Narrow(_store.Query(), Descriptor, RecordQuery.ParseFilters(filter));
        var buckets = await RecordAggregate.RunAsync(query, Descriptor, op, field, groupBy, ct);

        var items = new JsonArray();
        foreach (var bucket in buckets)
            items.Add(new JsonObject { ["key"] = bucket.Key, ["value"] = bucket.Value });

        return Ok(new JsonObject { ["op"] = op, ["buckets"] = items });
    }

    [HttpGet("{id}")]
    public virtual async Task<IActionResult> Get(string id, CancellationToken ct = default)
    {
        if (!Access.Read) return Denied("read");

        var record = await _store.FindAsync(id, ct);
        if (record is null) throw RecordException.NotFound(Descriptor.EntityKey, id);

        return Ok(RecordVisibility.Project(Access, record, Json));
    }

    [HttpPost]
    public virtual async Task<IActionResult> Create([FromBody] JsonElement body, CancellationToken ct = default)
    {
        if (!Access.Create) return Denied("create");

        var (record, supplied) = Read(body);
        Refuse(supplied);

        var created = await _store.CreateAsync(record, ct);
        return Created($"{Request.Path}/{created.Id}", RecordVisibility.Project(Access, created, Json));
    }

    /// <summary>
    /// Replace a record. Every field the entity has is written, so a field absent from the body is
    /// cleared — which is what replace means, and why <see cref="Patch"/> exists beside it.
    /// </summary>
    [HttpPut("{id}")]
    public virtual Task<IActionResult> Put(string id, [FromBody] JsonElement body, CancellationToken ct = default) =>
        Write(id, body, Descriptor.FieldKeys, ct);

    /// <summary>Change the fields the body names and leave the rest alone.</summary>
    [HttpPatch("{id}")]
    public virtual Task<IActionResult> Patch(string id, [FromBody] JsonElement body, CancellationToken ct = default) =>
        Write(id, body, SuppliedKeys(body), ct);

    [HttpDelete("{id}")]
    public virtual async Task<IActionResult> Delete(string id, CancellationToken ct = default)
    {
        if (!Access.Delete) return Denied("delete");

        await _store.DeleteAsync(id, ct);
        return NoContent();
    }

    private async Task<IActionResult> Write(string id, JsonElement body, IReadOnlyList<string> fields, CancellationToken ct)
    {
        if (!Access.Update) return Denied("update");

        var (record, supplied) = Read(body);

        // Checked against what the CALLER SENT, not against the field list being written. A replace
        // writes every field by definition, and refusing it because the payload happened to include
        // one the role may not set is right; refusing it because the entity HAS such a field would
        // make replace impossible for that role rather than merely restricted.
        Refuse(supplied);

        var updated = await _store.UpdateAsync(id, record, fields, ct);
        return Ok(RecordVisibility.Project(Access, updated, Json));
    }

    /// <summary>The body as an entity, plus the field keys it actually named. Both are needed: the
    /// entity to carry values, the key list because "absent" and "explicitly null" are different
    /// requests and deserialisation cannot tell them apart.</summary>
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

    private static IReadOnlyList<string> SuppliedKeys(JsonElement body) =>
        body.ValueKind == JsonValueKind.Object
            ? [.. body.EnumerateObject().Select(p => p.Name)]
            : [];

    /// <summary>Refuse a write that touches fields this role may not set — all of them at once.</summary>
    private void Refuse(IReadOnlyList<string> supplied)
    {
        var rejected = RecordVisibility.RejectedWrites(Access, supplied);
        if (rejected.Count > 0) throw RecordException.WriteRestricted(rejected);
    }

    /// <summary>
    /// Run a command against one record.
    ///
    /// <para>A command is not an update with a different name: it is a business action the
    /// definition declared, with its own permission, its own legality rule about which states it may
    /// run from, and its own required input. All three are checked before anything is written — see
    /// <see cref="Commands.CommandService{T}"/> for why that order matters.</para>
    ///
    /// <para><c>CommandService</c> arrives per action rather than through the constructor so that
    /// every generated controller stays the same six lines. An entity with no commands never
    /// resolves it.</para>
    /// </summary>
    [HttpPost("{id}/commands/{command}")]
    public virtual async Task<IActionResult> RunCommand(
        string id,
        string command,
        [FromBody] JsonElement input,
        [FromServices] Commands.CommandService<T> commands,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(commands);

        var result = await commands.RunAsync(id, command, input, Access, ct);
        return Ok(new JsonObject
        {
            ["record"] = result.Record,
            ["message"] = result.Message,
        });
    }

    /// <summary>403 rather than 404. Hiding an entity's existence from somebody who already knows
    /// its route buys nothing here — every route is in the application's own generated client.</summary>
    protected IActionResult Denied(string operation) =>
        StatusCode(403, this.Refuse(
            $"record.{operation}_denied",
            $"Your role may not {operation} {Descriptor.Label}."));

    /// <summary>A ceiling on <c>take</c>, so one request cannot ask for the whole table.</summary>
    protected const int MaxPageSize = 500;
}

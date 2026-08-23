// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json;
using Cordango.Standalone.Data;
using Cordango.Standalone.Records;
using Cordango.Standalone.Security;
using Microsoft.AspNetCore.Mvc;

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
///     public ExpenseController(RecordGateway&lt;Expense&gt; records) : base(records) { }
/// }
/// </code>
///
/// <para>The framework this pattern came from conjured that class at startup instead — a feature
/// provider closing the generic over every discovered type, plus a route convention rewriting the
/// route from an attribute. It is clever, and for a library meeting its entities at runtime it is
/// the only option. We meet ours at build time, so we write the six lines. What that buys is a route
/// table you can read, a controller you can put a breakpoint in, and a place for the first
/// hand-written endpoint to go that does not require understanding the conjuring first.</para>
///
/// <para><b>What is deliberately NOT here.</b> Whether a caller may read a field, what a refusal is
/// called, how a partial update differs from a replace: all of that lives in
/// <see cref="RecordGateway{T}"/>, because HTTP is not the only way into this application any more.
/// An MCP client goes through the same gateway, so it reaches exactly what the same person reaches
/// here — rather than through a second implementation of the same rules that drifts from this one.
/// What is left is the part that really is about HTTP: routes, status codes, and turning a query
/// string into filters.</para>
/// </summary>
public abstract class RecordsController<T> : ControllerBase where T : class, IRecord, new()
{
    private readonly RecordGateway<T> _records;

    protected RecordsController(RecordGateway<T> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        _records = records;
    }

    /// <summary>What this caller may do here.</summary>
    protected EntityAccess Access => Records.Access;

    /// <summary>The permission-applying façade over this entity's store. Protected so that a
    /// hand-written endpoint added beside these gets the same enforcement rather than reaching for
    /// the raw store.</summary>
    protected virtual RecordGateway<T> Records => _records;

    /// <summary>
    /// A page of records, narrowed and ordered by the request.
    ///
    /// <para><c>filter</c> may be repeated: <c>?filter=status:eq:open&amp;filter=amount:gt:100</c>.
    /// <c>sort</c> is a comma-separated list where a leading minus means descending:
    /// <c>?sort=-spent_on,description</c>.</para>
    /// </summary>
    [HttpGet]
    public virtual async Task<IActionResult> List(
        [FromQuery(Name = "filter")] string[]? filter = null,
        [FromQuery] string? sort = null,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken ct = default) =>
        Ok(await Records.ListAsync(
            RecordQuery.ParseFilters(filter), RecordQuery.ParseSort(sort), skip, take, ct));

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
        CancellationToken ct = default) =>
        Ok(await Records.AggregateAsync(op, field, groupBy, RecordQuery.ParseFilters(filter), ct));

    [HttpGet("{id}")]
    public virtual async Task<IActionResult> Get(string id, CancellationToken ct = default) =>
        Ok(await Records.GetAsync(id, ct));

    [HttpPost]
    public virtual async Task<IActionResult> Create([FromBody] JsonElement body, CancellationToken ct = default)
    {
        var created = await Records.CreateAsync(body, ct);
        return Created($"{Request.Path}/{created["id"]}", created);
    }

    /// <summary>
    /// Replace a record. Every field the entity has is written, so a field absent from the body is
    /// cleared — which is what replace means, and why <see cref="Patch"/> exists beside it.
    /// </summary>
    [HttpPut("{id}")]
    public virtual async Task<IActionResult> Put(
        string id, [FromBody] JsonElement body, CancellationToken ct = default) =>
        Ok(await Records.WriteAsync(id, body, Records.FieldKeys, ct));

    /// <summary>Change the fields the body names and leave the rest alone.</summary>
    [HttpPatch("{id}")]
    public virtual async Task<IActionResult> Patch(
        string id, [FromBody] JsonElement body, CancellationToken ct = default) =>
        Ok(await Records.WriteAsync(id, body, Records.SuppliedKeys(body), ct));

    [HttpDelete("{id}")]
    public virtual async Task<IActionResult> Delete(string id, CancellationToken ct = default)
    {
        await Records.DeleteAsync(id, ct);
        return NoContent();
    }

    /// <summary>
    /// Run a command against one record.
    ///
    /// <para>A command is not an update with a different name: it is a business action the
    /// definition declared, with its own permission, its own legality rule about which states it may
    /// run from, and its own required input. All three are checked before anything is written — see
    /// <see cref="Commands.CommandService{T}"/> for why that order matters.</para>
    /// </summary>
    [HttpPost("{id}/commands/{command}")]
    public virtual async Task<IActionResult> RunCommand(
        string id,
        string command,
        [FromBody] JsonElement input,
        CancellationToken ct = default)
    {
        var result = await Records.RunCommandAsync(id, command, input, ct);
        return Ok(new System.Text.Json.Nodes.JsonObject
        {
            ["record"] = result.Record,
            ["message"] = result.Message,
        });
    }

    /// <summary>A ceiling on <c>take</c>, so one request cannot ask for the whole table.</summary>
    protected const int MaxPageSize = RecordGateway<T>.MaxPageSize;
}

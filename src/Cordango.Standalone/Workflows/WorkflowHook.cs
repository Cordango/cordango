// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json;
using System.Text.Json.Nodes;
using Cordango.Standalone.Data;
using Cordango.Standalone.Hooks;
using Cordango.Standalone.Records;

namespace Cordango.Standalone.Workflows;

/// <summary>
/// What connects a write to the workflows watching for it.
///
/// <para>Registered for every entity, once, generically — not emitted per entity. Nothing about it
/// varies with the entity except the key, which the descriptor already carries, and an application
/// with twenty entities does not need twenty copies of six lines.</para>
///
/// <para><b>It runs AFTER the save.</b> The record exists, its id is real, and a condition asking
/// about it gets the truth rather than a proposal. The cost is that an effect writing back to the
/// same record is a second write — deliberate, and cheaper than the alternative, which is every rule
/// reasoning about a row that might still fail to save.</para>
/// </summary>
public sealed class WorkflowHook<T> : IAfterCreate<T>, IAfterUpdate<T> where T : class, IRecord, new()
{
    private readonly IServiceProvider _services;
    private readonly RecordDescriptor<T> _descriptor;

    /// <summary>
    /// The runner is fetched when a write happens, not when this is built — and that is not a
    /// convenience, it is the only way round a cycle that is real.
    ///
    /// <para>A workflow writes through stores, and every store notifies workflows. So the runner
    /// needs every entity's writer, a writer needs that entity's store, a store needs its hooks, and
    /// one of its hooks is this. Asking the container to construct that graph is asking it to build
    /// a ring, and it refuses — loudly, at startup, which is how this was found.</para>
    ///
    /// <para>Deferring HERE rather than in the runner is the honest place: a hook exists to run
    /// after the fact, so it genuinely does not need the runner until a record has been written.
    /// The runner is scoped, so this resolves once per request rather than once per record.</para>
    /// </summary>
    public WorkflowHook(IServiceProvider services, RecordDescriptor<T> descriptor)
    {
        _services = services;
        _descriptor = descriptor;
    }

    private WorkflowRunner Runner =>
        (WorkflowRunner)_services.GetService(typeof(WorkflowRunner))!;

    public Task AfterCreateAsync(T record, RecordContext context, CancellationToken ct) =>
        Runner.CreatedAsync(_descriptor.EntityKey, Snapshot(record), ct);

    public Task AfterUpdateAsync(T record, T before, RecordContext context, CancellationToken ct) =>
        Runner.UpdatedAsync(_descriptor.EntityKey, Snapshot(record), Snapshot(before), ct);

    private static JsonObject Snapshot(T record) =>
        JsonSerializer.SerializeToNode(record, Json)?.AsObject() ?? [];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

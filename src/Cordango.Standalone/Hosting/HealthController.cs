// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.Standalone.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cordango.Standalone.Hosting;

/// <summary>
/// Is this instance able to serve requests.
///
/// <para><b>It asks the database.</b> A health check that only proves the process is listening will
/// report a healthy instance for as long as the process survives, which is precisely as long as an
/// orchestrator will keep routing traffic to an instance that cannot answer a single query. The
/// query is <c>CanConnectAsync</c> — enough to prove the connection works, cheap enough to run every
/// few seconds forever.</para>
///
/// <para>Anonymous, because a load balancer has no credentials, and it reveals only whether the
/// database is reachable — never which database, where, or why not.</para>
/// </summary>
[Route("health")]
public sealed class HealthController : ControllerBase
{
    private readonly CordangoDbContext _db;

    public HealthController(CordangoDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var database = await _db.Database.CanConnectAsync(ct);
        return database
            ? Ok(new { status = "ok", database = true })
            : StatusCode(503, new { status = "degraded", database = false });
    }
}

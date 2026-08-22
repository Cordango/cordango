// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Microsoft.AspNetCore.Mvc;

namespace Cordango.Standalone.Http;

/// <summary>
/// Answers any <c>/api/…</c> route no controller claimed.
///
/// <para><b>Why it exists at all.</b> A generated application serves a single-page app, so anything
/// the server does not recognise falls back to <c>index.html</c> — which is right for
/// <c>/expenses/42</c> and badly wrong for <c>/api/expence</c>. Without this, a typo in an endpoint
/// answers 200 with a page of HTML, and the client's <c>fetch</c> reports a JSON parse error at
/// character 0. Hours have been lost to that error message, because it describes the symptom in a
/// place with no connection to the cause.</para>
///
/// <para>A catch-all route has lower precedence than every literal one, so this only ever runs when
/// nothing else matched.</para>
/// </summary>
[Route("api/{**path}")]
public sealed class ApiNotFoundController : ControllerBase
{
    [HttpGet, HttpPost, HttpPut, HttpPatch, HttpDelete]
    public IActionResult Missing(string? path) =>
        NotFound(this.Refuse("route.not_found", $"No API endpoint at /api/{path}."));
}

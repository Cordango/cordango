// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.Standalone.Http;
using Microsoft.AspNetCore.Http;
using Cordango.Standalone.Records;
using Microsoft.AspNetCore.Mvc;

namespace Cordango.Standalone.Media;

/// <summary>
/// Uploading and fetching the files that attachment fields point at.
///
/// <para>Signing in is the whole of the read rule, and that is a deliberate simplification worth
/// stating plainly: a reference is a 64-character content hash, so it cannot be guessed, but anyone
/// signed in who HAS a reference can fetch the file behind it — including one attached to a record
/// their role cannot read. Tying a file to the record that points at it would fix that, and it needs
/// the reverse index that S7 generates. Until then the limit is documented rather than implied.</para>
/// </summary>
[Route("api/media")]
public sealed class MediaController : ControllerBase
{
    private readonly IFileStore _files;
    private readonly ICurrentUser _user;

    public MediaController(IFileStore files, ICurrentUser user)
    {
        _files = files;
        _user = user;
    }

    [HttpPost]
    public async Task<IActionResult> Upload(IFormFile? file, CancellationToken ct)
    {
        if (_user.UserId is null) return Unauthorized(new ApiError("auth.required", "Sign in to upload files."));
        if (file is null || file.Length == 0)
            throw new RecordException("media.empty", "No file was sent.");

        await using var content = file.OpenReadStream();
        var stored = await _files.SaveAsync(content, file.FileName, file.ContentType, ct);
        return Ok(stored);
    }

    [HttpGet("{reference}")]
    public async Task<IActionResult> Download(string reference, CancellationToken ct)
    {
        if (_user.UserId is null) return Unauthorized(new ApiError("auth.required", "Sign in to fetch files."));

        var found = await _files.OpenAsync(reference, ct);
        if (found is not var (meta, content) || content is null)
            throw new RecordException("media.not_found", "No file with that reference.", 404);

        // The uploader chose this content type, so the browser must not be allowed to second-guess
        // it into something executable. Without the nosniff header a file uploaded as text/plain can
        // still be sniffed as HTML and run as script on this origin.
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(content, meta.ContentType, meta.FileName);
    }

    [HttpDelete("{reference}")]
    public async Task<IActionResult> Delete(string reference, CancellationToken ct)
    {
        if (!_user.IsAdministrator)
            return StatusCode(403, new ApiError("media.delete_denied", "Only an administrator may delete files."));

        // Content addressing means one file can be the target of many records' fields. Deleting it
        // is therefore an administrative act with consequences the caller cannot see from here, and
        // the answer says whether anything was actually removed.
        var removed = await _files.DeleteAsync(reference, ct);
        return Ok(new { removed });
    }
}

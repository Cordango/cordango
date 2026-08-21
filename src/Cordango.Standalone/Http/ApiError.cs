// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace Cordango.Standalone.Http;

/// <summary>
/// The error body, and the only shape this application returns for one.
///
/// <para><c>code</c> is stable and machine-readable — a client switches on it, and it never changes
/// because somebody improved the wording. <c>error</c> is the sentence a person reads, already in
/// their language: the boundary decides the language, the code travels through the domain.</para>
/// </summary>
/// <param name="Code">A dotted key such as <c>record.not_found</c>.</param>
/// <param name="Error">The translated message.</param>
/// <param name="Fields">Named when the failure is about particular fields, so a form can mark
/// them.</param>
public sealed record ApiError(string Code, string Error, IReadOnlyList<string>? Fields = null);

/// <summary>
/// A refusal with an answer already in it: the status to send and the code to send it under.
///
/// <para>Hooks and stores throw this. Everything else that goes wrong is a bug and comes out as a
/// 500 with no detail on the wire — the prior art returned <c>ex.Message</c> to the caller from both
/// its controller and its exception middleware, which turns any unhandled null reference into a free
/// tour of the codebase.</para>
/// </summary>
public class RecordException : Exception
{
    public RecordException(string code, string message, int statusCode = 400, IReadOnlyList<string>? fields = null)
        : base(message)
    {
        Code = code;
        StatusCode = statusCode;
        Fields = fields;
    }

    public RecordException() : this("record.invalid", "The request was refused.") { }

    public RecordException(string message) : this("record.invalid", message) { }

    public RecordException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = "record.invalid";
        StatusCode = 400;
    }

    public string Code { get; } = "record.invalid";
    public int StatusCode { get; } = 400;
    public IReadOnlyList<string>? Fields { get; }

    public ApiError ToApiError(string? translated = null) => new(Code, translated ?? Message, Fields);

    public static RecordException NotFound(string entityKey, string id) =>
        new("record.not_found", $"No {entityKey} with id '{id}'.", 404);

    public static RecordException Forbidden(string code, string message) => new(code, message, 403);

    /// <summary>The caller sent fields their role may not write. Named all at once, because
    /// discovering them one round trip at a time is a worse experience than one list.</summary>
    public static RecordException WriteRestricted(IReadOnlyList<string> fields) =>
        new("record.field_write_denied",
            "Your role may not set: " + string.Join(", ", fields) + ".",
            403,
            fields);
}

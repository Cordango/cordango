// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace Cordango.Standalone.Records;

/// <summary>
/// One stored row of one entity. Every generated entity class implements this and nothing else —
/// it is a plain data object, which is what makes it safe for the generator to overwrite on every
/// build.
///
/// <para><b>Why the id is a string.</b> Cordango record ids are <c>text</c> in the data plane, not
/// integers and not real uuids: an id may be a generated key, but it may equally be a stable handle
/// somebody typed. Making the key type a parameter would put a second type argument on the store,
/// the controller and every hook in exchange for a choice no definition can express.</para>
/// </summary>
public interface IRecord
{
    string Id { get; set; }
}

/// <summary>
/// Who wrote a record and when. Generated entities implement this when the application asks for
/// tracking, and the values are stamped in exactly one place —
/// <see cref="Data.CordangoDbContext.SaveChanges()"/> — over
/// <c>ChangeTracker.Entries&lt;IHasTrackingFields&gt;()</c>.
///
/// <para><b>One place, and a typed one.</b> The obvious alternative is for each write path to check
/// whether the entity wants tracking and stamp it. The prior art we assessed did exactly that, with
/// a hand-written <c>typeof(T).IsAssignableFrom(typeof(IHasTrackingFields))</c> at six call sites —
/// operands reversed, so the condition was false for every entity and no application using it ever
/// recorded who created a row. Nothing failed; the columns were simply always null. A generic
/// <c>Entries&lt;T&gt;()</c> cannot be written the wrong way round and compile.</para>
/// </summary>
public interface IHasTrackingFields
{
    DateTimeOffset Created { get; set; }
    string? CreatedBy { get; set; }
    DateTimeOffset? LastModified { get; set; }
    string? LastModifiedBy { get; set; }
}

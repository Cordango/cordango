// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace Cordango.Standalone.Records;

/// <summary>
/// Who is making this request, reduced to the two facts the runtime actually needs: an id to stamp
/// on rows, and the role keys that decide what those rows may contain.
///
/// <para><b>Why an interface and not <c>ClaimsPrincipal</c>.</b> Authentication in a generated
/// application is stock ASP.NET Core Identity, and this library deliberately does not depend on it.
/// Keeping the seam this narrow means the store, the hooks and the permission code can all be
/// unit-tested with a two-line fake, and means somebody who replaces Identity with their own
/// single-sign-on has one class to write rather than a framework to satisfy.</para>
/// </summary>
public interface ICurrentUser
{
    /// <summary>The stable id stamped into <see cref="IHasTrackingFields.CreatedBy"/>. Null for
    /// unauthenticated work such as a seed run or a scheduled job.</summary>
    string? UserId { get; }

    /// <summary>
    /// Which directory Person this login is, when it is one.
    ///
    /// <para>Separate from <see cref="UserId"/> because a login and a person are separate things.
    /// A definition's <c>{{actor.id}}</c> means the PERSON — "expenses I submitted", "tickets
    /// assigned to me" — and answering it with a login id would compare an identity-table key
    /// against a directory reference and match nothing, silently, on every such view.</para>
    /// </summary>
    string? PersonId { get; }

    /// <summary>Role keys as the App Definition spells them. Empty means no access at all — this is
    /// not a "logged in so probably fine" default.</summary>
    IReadOnlyCollection<string> RoleKeys { get; }

    /// <summary>Bypasses role evaluation entirely. The application's own administrators, and the
    /// seed runner. Kept as one flag rather than a magic role key so that reading a definition never
    /// turns up a role that grants more than it says.</summary>
    bool IsAdministrator { get; }
}

/// <summary>Reading the clock, as a dependency. Every timestamp the runtime writes comes through
/// here so a test can assert an exact value rather than a range, and so a seed run can anchor a whole
/// dataset to one instant.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>The real clock.</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>Nobody in particular: no id, no roles, no bypass. The default registration, so an
/// application that forgets to wire authentication fails closed rather than open.</summary>
public sealed class AnonymousUser : ICurrentUser
{
    public string? UserId => null;
    public string? PersonId => null;
    public IReadOnlyCollection<string> RoleKeys => [];
    public bool IsAdministrator => false;
}

// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.Standalone.Hooks;

namespace Cordango.Standalone.Custom;

/// <summary>
/// Marks a class whose methods a computed expression may call as <c>custom.&lt;name&gt;(…)</c>.
///
/// <para><b>Required, and that is the point.</b> A lone <see cref="CordangoFunctionAttribute"/>
/// inside an unmarked class is an error rather than a quiet no-op, so a file says what it is from
/// its first line — and so the scanner can skip everything else in the directory without reading
/// it. Helper types beside these are ordinary C# and are left alone.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class CordangoFunctionsAttribute : Attribute;

/// <summary>
/// One function a computed expression may call.
///
/// <para>The <paramref name="name"/> is the name the EXPRESSION uses, deliberately separate from
/// the C# method name: a formula is written by whoever describes the app, in the app's own words,
/// and renaming a method should not rewrite every expression that calls it.</para>
///
/// <para><b>Pure and synchronous, by contract.</b> A computed field is worked out when the row is
/// written and stored beside it, and the recompute cascade may work one out many times; anything
/// that reads a clock, a random number or the outside world makes the stored figure disagree with
/// itself. The build refuses the ones it can see statically, and cannot see through a helper — so
/// this is a promise you keep, not one the compiler keeps for you.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class CordangoFunctionAttribute(string name) : Attribute
{
    /// <summary>What an expression calls: lower case, digits and underscores.</summary>
    public string Name { get; } = name;

    /// <summary>What it is for, in a sentence. Shown wherever the app's own vocabulary is.</summary>
    public string? Description { get; set; }
}

/// <summary>
/// Marks a class whose methods run around a record write.
///
/// <para>Instance methods, so the class can take its dependencies through its constructor the way
/// any other service does. It is registered scoped, once, however many hooks it carries.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class CordangoHooksAttribute : Attribute;

/// <summary>
/// Where a BEFORE hook sits relative to the computed-field pass.
///
/// <para>Only the two before-hooks have a position to take. The after-hooks and both delete hooks
/// run once the write is already decided, which is why neither of their attributes offers this:
/// a stage that could be written and would then be ignored is worse than one that cannot be
/// written at all.</para>
/// </summary>
public enum HookStage
{
    /// <summary>Before the figures are worked out, so a hook can set a value the formulas then
    /// read. The default, because supplying an input is the commoner reason to write one.</summary>
    BeforeComputed,

    /// <summary>After the figures are worked out, so a hook can read what they came to. Anything
    /// it writes that a formula depends on is not seen until the next write.</summary>
    AfterComputed,
}

/// <summary>Runs before a record is inserted. Change the record in place; throw to refuse the
/// write. Adapts to <see cref="IBeforeCreate{T}"/>.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class BeforeCreateAttribute : Attribute
{
    public HookStage Stage { get; set; } = HookStage.BeforeComputed;
}

/// <summary>Runs before an update is saved, with the incoming record and a detached copy of the row
/// as it was. Adapts to <see cref="IBeforeUpdate{T}"/>.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class BeforeUpdateAttribute : Attribute
{
    public HookStage Stage { get; set; } = HookStage.BeforeComputed;
}

/// <summary>Runs before a delete. Throw to refuse it. Adapts to
/// <see cref="IBeforeDelete{T}"/>.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class BeforeDeleteAttribute : Attribute;

/// <summary>Runs after the insert has been saved. Nothing here can veto a write that already
/// happened. Adapts to <see cref="IAfterCreate{T}"/>.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class AfterCreateAttribute : Attribute;

/// <summary>Runs after an update has been saved, with both versions in hand. Adapts to
/// <see cref="IAfterUpdate{T}"/>.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class AfterUpdateAttribute : Attribute;

/// <summary>Runs after a delete has been saved. Adapts to
/// <see cref="IAfterDelete{T}"/>.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class AfterDeleteAttribute : Attribute;

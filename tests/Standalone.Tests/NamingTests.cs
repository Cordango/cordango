// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.SourceGen.DotNetVue.Model;

namespace Cordango.Standalone.Tests;

/// <summary>
/// A business noun is not a naming problem, and the generator does not get to say otherwise.
///
/// <para>"Task", "Group", "File", "Person", "Directory" are all ordinary things to build an
/// application about, and every one of them is already a type in scope in the files the generator
/// writes. The rule is the one <see cref="Naming.Property"/> already uses: suffix, and only when it
/// is needed, so the ordinary case reads exactly like the definition.</para>
/// </summary>
public class NamingTests
{
    [Theory]
    [InlineData("expense_claim", "Expenses", "ExpenseClaim")]
    [InlineData("booking", "RoomBooking", "Booking")]
    [InlineData("project", "TaskManager", "Project")]
    public void An_ordinary_name_is_left_alone(string key, string ns, string expected) =>
        Assert.Equal(expected, Naming.Type(key, ns));

    /// <summary>An entity whose name matches the application's own namespace. Every use site
    /// resolves it to the namespace: "TimeOff is a namespace but is used like a type", fourteen
    /// times, in an application nobody would look at twice.</summary>
    [Fact]
    public void A_name_that_is_the_applications_namespace_is_suffixed() =>
        Assert.Equal("TimeOffRecord", Naming.Type("time_off", "TimeOff"));

    /// <summary>Every generated file imports <c>System.Threading.Tasks</c> implicitly, so an entity
    /// called "task" makes the word ambiguous in the files that need both meanings — a controller
    /// returns <c>Task&lt;IActionResult&gt;</c> over rows of a task.</summary>
    [Fact]
    public void A_name_that_a_framework_type_already_holds_is_suffixed() =>
        Assert.Equal("TaskRecord", Naming.Type("task", "TaskManager"));

    /// <summary>The namespaces the generator itself writes into. An entity keyed "data" would be
    /// ambiguous with <c>{App}.Data</c> from every file in the application.</summary>
    [Theory]
    [InlineData("data", "DataRecord")]
    [InlineData("commands", "CommandsRecord")]
    [InlineData("security", "SecurityRecord")]
    public void A_name_the_generator_uses_for_a_namespace_is_suffixed(string key, string expected) =>
        Assert.Equal(expected, Naming.Type(key, "Anything"));

    /// <summary>The built-in directory is imported by generated files, so an application that
    /// models its own people cannot call the class <c>Person</c>.</summary>
    [Theory]
    [InlineData("person", "PersonRecord")]
    [InlineData("organization", "OrganizationRecord")]
    [InlineData("group", "GroupRecord")]
    public void A_name_the_runtime_directory_holds_is_suffixed(string key, string expected) =>
        Assert.Equal(expected, Naming.Type(key, "Anything"));

    /// <summary>The suffix is applied once and deterministically, so two builds of the same
    /// definition produce the same class name — the property the whole toolchain rests on.</summary>
    [Fact]
    public void The_suffix_is_stable() =>
        Assert.Equal(Naming.Type("task", "TaskManager"), Naming.Type("task", "TaskManager"));
}

// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.SourceGen.DotNetVue.Model;

namespace Cordango.Standalone.Tests;

public class NamingTests
{
    [Theory]
    [InlineData("expense_claim", "Expenses", "ExpenseClaim")]
    [InlineData("booking", "RoomBooking", "Booking")]
    [InlineData("project", "TaskManager", "Project")]
    public void An_ordinary_name_is_left_alone(string key, string ns, string expected) =>
        Assert.Equal(expected, Naming.Type(key, ns));

    [Fact]
    public void A_name_that_is_the_applications_namespace_is_suffixed() =>
        Assert.Equal("TimeOffRecord", Naming.Type("time_off", "TimeOff"));

    [Fact]
    public void A_name_that_a_framework_type_already_holds_is_suffixed() =>
        Assert.Equal("TaskRecord", Naming.Type("task", "TaskManager"));

    [Theory]
    [InlineData("data", "DataRecord")]
    [InlineData("commands", "CommandsRecord")]
    [InlineData("security", "SecurityRecord")]
    public void A_name_the_generator_uses_for_a_namespace_is_suffixed(string key, string expected) =>
        Assert.Equal(expected, Naming.Type(key, "Anything"));

    [Theory]
    [InlineData("person", "PersonRecord")]
    [InlineData("organization", "OrganizationRecord")]
    [InlineData("group", "GroupRecord")]
    public void A_name_the_runtime_directory_holds_is_suffixed(string key, string expected) =>
        Assert.Equal(expected, Naming.Type(key, "Anything"));

    [Fact]
    public void The_suffix_is_stable() =>
        Assert.Equal(Naming.Type("task", "TaskManager"), Naming.Type("task", "TaskManager"));
}

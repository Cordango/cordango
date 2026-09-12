// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.SourceGen;

namespace Cordango.Compatibility.Tests;

/// <summary>
/// The one thing Cordango Platform cannot do that a generated application can.
///
/// <para>Everything else in the CORD21xx range runs the other way — the platform keeps an audit
/// trail and a standalone build does not, the platform has other applications to reference and a
/// standalone build does not. This is the single case where the generated application is the more
/// capable of the two, because it has a compiler and the platform has an interpreter.</para>
/// </summary>
public class PlatformRefusalTests
{
    private static JsonObject Definition(JsonObject? custom)
    {
        var document = new JsonObject { ["key"] = "billing", ["name"] = "Billing" };
        if (custom is not null) document["custom"] = custom;
        return document;
    }

    private static JsonObject WithFunction() => new()
    {
        ["language"] = "dotnet",
        ["hash"] = "sha256:" + new string('a', 64),
        ["functions"] = new JsonArray(new JsonObject { ["name"] = "discount" }),
    };

    [Fact]
    public void An_application_with_no_custom_code_runs_on_the_platform()
    {
        Assert.Empty(PlatformCapabilities.Validate(Definition(null)));
    }

    [Fact]
    public void A_custom_function_is_refused_and_says_why_the_platform_cannot()
    {
        var refusals = PlatformCapabilities.Validate(Definition(WithFunction()));

        var refusal = Assert.Single(refusals);
        Assert.Equal(DiagnosticCodes.CustomCode, refusal.Code);
        Assert.Contains("interpreting its definition", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("no compiler in the runtime", refusal.Message, StringComparison.Ordinal);
        Assert.Equal("$.custom", refusal.JsonPath);
    }

    [Fact]
    public void A_custom_hook_is_refused_too()
    {
        var custom = new JsonObject
        {
            ["language"] = "dotnet",
            ["hash"] = "sha256:" + new string('a', 64),
            ["hooks"] = new JsonArray(new JsonObject { ["entity"] = "invoice" }),
        };

        Assert.Single(PlatformCapabilities.Validate(Definition(custom)));
    }

    [Fact]
    public void A_section_that_declares_nothing_refuses_nothing()
    {
        // An application whose custom/ directory exists but holds no callable declaration has
        // nothing the platform would fail to run.
        var empty = new JsonObject
        {
            ["language"] = "dotnet",
            ["hash"] = "sha256:" + new string('a', 64),
        };

        Assert.Empty(PlatformCapabilities.Validate(Definition(empty)));
    }

    [Fact]
    public void The_refusal_names_the_language_so_the_reader_knows_which_code()
    {
        var refusal = Assert.Single(PlatformCapabilities.Validate(Definition(WithFunction())));

        Assert.Contains("dotnet", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_that_is_not_a_definition_throws()
    {
        Assert.Empty(PlatformCapabilities.Validate(null));
        Assert.Empty(PlatformCapabilities.Validate(JsonValue.Create(3)));
    }
}

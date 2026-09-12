// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Nodes;
using Cordango.SourceGen;
using Cordango.SourceGen.Common;
using Cordango.SourceGen.DotNet.Emit;

namespace Cordango.Standalone.Tests;

/// <summary>
/// What a formula calling the application's own code compiles to, and what stops the application
/// being compiled from code the definition was not written against.
/// </summary>
public class CustomCodeEmitTests
{
    private static JsonObject Custom(string hash = "sha256:" + "a") => new()
    {
        ["language"] = "dotnet",
        ["hash"] = hash,
        ["functions"] = new JsonArray
        {
            new JsonObject
            {
                ["name"] = "surcharge",
                ["returns"] = "number",
                ["params"] = new JsonArray
                {
                    new JsonObject { ["name"] = "total", ["kind"] = "number" },
                },
                ["source"] = new JsonObject
                {
                    ["file"] = "Pricing.cs", ["type"] = "Pricing", ["method"] = "Surcharge",
                },
            },
        },
    };

    private static (AppModel App, EntityModel Entity) Build(JsonObject? custom)
    {
        var entity = new JsonObject
        {
            ["key"] = "order",
            ["label"] = "Order",
            ["fields"] = new JsonArray
            {
                new JsonObject { ["key"] = "total", ["label"] = "Total", ["type"] = "decimal" },
                new JsonObject { ["key"] = "result", ["label"] = "Result", ["type"] = "decimal" },
            },
        };

        var manifest = new JsonObject
        {
            ["key"] = "billing",
            ["name"] = "Billing",
            ["entities"] = new JsonArray(entity.DeepClone()),
        };

        if (custom is not null) manifest["custom"] = custom;

        var app = AppModel.From(new CompiledAppArtifact(
            manifest, manifest, "unhashed", new CompilerInfo("test", "1")));

        return (app, new EntityModel(entity, "Billing"));
    }

    private static FieldModel Field(string expr) => new(new JsonObject
    {
        ["key"] = "result",
        ["label"] = "Result",
        ["type"] = "decimal",
        ["computed"] = new JsonObject { ["expr"] = expr },
    }, "order");

    [Fact]
    public void A_call_compiles_to_a_fully_qualified_invocation()
    {
        var (app, entity) = Build(Custom());

        var written = ComputedEmitter.Expression(app, entity, Field("custom.surcharge(total)"));

        Assert.NotNull(written);
        Assert.Contains("global::Billing.Custom.Pricing.Surcharge(", written, StringComparison.Ordinal);
    }

    [Fact]
    public void A_call_composes_with_the_arithmetic_around_it()
    {
        var (app, entity) = Build(Custom());

        var written = ComputedEmitter.Expression(app, entity, Field("total + custom.surcharge(total)"));

        Assert.NotNull(written);
        Assert.Contains("global::Billing.Custom.Pricing.Surcharge", written, StringComparison.Ordinal);
        Assert.Contains("r.Total", written, StringComparison.Ordinal);
    }

    [Fact]
    public void An_application_that_declares_nothing_cannot_write_the_call()
    {
        var (app, entity) = Build(null);

        Assert.Null(ComputedEmitter.Expression(app, entity, Field("custom.surcharge(total)")));
    }

    [Fact]
    public void Sources_that_do_not_match_the_definition_stop_the_build()
    {
        var (app, _) = Build(Custom("sha256:aaa"));

        var bundle = new CustomSourceBundle("dotnet", "sha256:bbb",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["Pricing.cs"] = "// x" });

        var mismatch = CustomEmitter.Mismatch(app, bundle);

        Assert.NotNull(mismatch);
        Assert.Contains("changed after it was checked", mismatch.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Sources_in_the_wrong_language_say_which_two_disagree()
    {
        var (app, _) = Build(Custom("sha256:aaa"));

        var bundle = new CustomSourceBundle("typescript", "sha256:aaa",
            new Dictionary<string, string>(StringComparer.Ordinal));

        var mismatch = CustomEmitter.Mismatch(app, bundle);

        Assert.NotNull(mismatch);
        Assert.Contains("'dotnet'", mismatch.Message, StringComparison.Ordinal);
        Assert.Contains("'typescript'", mismatch.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_definition_that_declares_custom_code_cannot_be_built_without_it()
    {
        var (app, _) = Build(Custom());

        var mismatch = CustomEmitter.Mismatch(app, null);

        Assert.NotNull(mismatch);
        Assert.Contains("cannot be built on its own", mismatch.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Sources_nothing_declares_are_refused_rather_than_compiled_in()
    {
        var (app, _) = Build(null);

        var bundle = new CustomSourceBundle("dotnet", "sha256:aaa",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["Pricing.cs"] = "// x" });

        Assert.NotNull(CustomEmitter.Mismatch(app, bundle));
    }

    [Fact]
    public void An_application_with_neither_is_simply_fine()
    {
        var (app, _) = Build(null);

        Assert.Null(CustomEmitter.Mismatch(app, null));
    }

    private static JsonObject Hook(string @event, string method, string? stage = null)
    {
        var hook = new JsonObject
        {
            ["entity"] = "order",
            ["event"] = @event,
            ["source"] = new JsonObject
            {
                ["file"] = "Rules.cs", ["type"] = "Rules", ["method"] = method,
            },
        };

        if (stage is not null) hook["stage"] = stage;
        return hook;
    }

    private static AppModel WithHooks(params JsonObject[] hooks)
    {
        var custom = Custom();
        custom["hooks"] = new JsonArray([.. hooks.Select(h => (JsonNode)h)]);
        return Build(custom).App;
    }

    [Fact]
    public void A_create_hook_becomes_an_adapter_onto_the_runtime_interface()
    {
        var app = WithHooks(Hook("before_create", "Stamp"));

        var adapter = CustomEmitter.Adapters(app).Single();

        Assert.Equal("api/Hooks/CustomRulesStamp.cs", adapter.RelativePath);
        Assert.Contains(": IBeforeCreate<Order>", adapter.Content, StringComparison.Ordinal);
        Assert.Contains("public Task BeforeCreateAsync(Order record, RecordContext context, CancellationToken ct)",
            adapter.Content, StringComparison.Ordinal);
        Assert.Contains("custom.Stamp(record, context, ct);", adapter.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void An_update_adapter_passes_both_versions_through()
    {
        var app = WithHooks(Hook("before_update", "Check"));

        var adapter = CustomEmitter.Adapters(app).Single();

        Assert.Contains(": IBeforeUpdate<Order>", adapter.Content, StringComparison.Ordinal);
        Assert.Contains("Order record, Order before, RecordContext context", adapter.Content, StringComparison.Ordinal);
        Assert.Contains("custom.Check(record, before, context, ct);", adapter.Content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("after_create", "IAfterCreate", "AfterCreateAsync")]
    [InlineData("before_delete", "IBeforeDelete", "BeforeDeleteAsync")]
    [InlineData("after_delete", "IAfterDelete", "AfterDeleteAsync")]
    [InlineData("after_update", "IAfterUpdate", "AfterUpdateAsync")]
    public void Every_event_adapts_to_its_own_interface(string @event, string face, string method)
    {
        var app = WithHooks(Hook(@event, "Run"));

        var adapter = CustomEmitter.Adapters(app).Single();

        Assert.Contains($": {face}<Order>", adapter.Content, StringComparison.Ordinal);
        Assert.Contains($"public Task {method}(", adapter.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void A_hook_on_a_record_that_is_gone_emits_nothing_rather_than_broken_code()
    {
        var custom = Custom();
        custom["hooks"] = new JsonArray(new JsonObject
        {
            ["entity"] = "vanished",
            ["event"] = "before_create",
            ["source"] = new JsonObject
            {
                ["file"] = "Rules.cs", ["type"] = "Rules", ["method"] = "Stamp",
            },
        });

        Assert.Empty(CustomEmitter.Adapters(Build(custom).App));
    }

    [Fact]
    public void A_stage_decides_which_side_of_the_computed_pass_a_hook_sits()
    {
        var app = WithHooks(
            Hook("before_create", "Early"),
            Hook("before_create", "Late", "after_computed"));

        var hooks = app.CustomHooks;

        Assert.True(hooks[0].RunsBeforeComputed);
        Assert.False(hooks[1].RunsBeforeComputed);
    }

    [Fact]
    public void An_after_event_never_claims_to_run_before_the_computed_pass()
    {
        var app = WithHooks(Hook("after_create", "Log"));

        Assert.False(app.CustomHooks.Single().RunsBeforeComputed);
    }

    [Fact]
    public void Every_source_is_emitted_under_the_custom_directory()
    {
        var bundle = new CustomSourceBundle("dotnet", "sha256:aaa",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Pricing.cs"] = "// one",
                ["Hooks/Rules.cs"] = "// two",
            });

        var emitted = CustomEmitter.Emit(bundle).ToList();

        Assert.Equal(
            ["api/Custom/Hooks/Rules.cs", "api/Custom/Pricing.cs"],
            emitted.Select(f => f.RelativePath).OrderBy(p => p, StringComparer.Ordinal).ToArray());

        Assert.All(emitted, f => Assert.Contains("cordango build", f.Content, StringComparison.Ordinal));
    }
}

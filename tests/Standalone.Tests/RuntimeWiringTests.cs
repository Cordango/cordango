// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.Standalone.Data;
using Cordango.Standalone.Hosting;
using Cordango.Standalone.Http;
using Cordango.Standalone.Directory;
using Cordango.Standalone.Records;
using Cordango.Standalone.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cordango.Standalone.Tests;

/// <summary>
/// The container actually builds what a generated application asks it for.
///
/// <para>Compiling proves the registrations are valid C#. It does not prove that
/// <c>IRecordStore&lt;Person&gt;</c> can be constructed — that needs six hook collections, a
/// descriptor, a clock, a user and a context to all be registered, and a missing one shows up as an
/// exception on the first request rather than at build time. This is the cheapest place to find
/// out.</para>
/// </summary>
public class RuntimeWiringTests
{
    /// <summary>
    /// Builds the container the way a generated application does: through the web host builder, not
    /// through a bare <c>ServiceCollection</c>.
    ///
    /// <para>The bare collection cannot validate — MVC's own infrastructure wants an
    /// <c>IWebHostEnvironment</c>, which only a host provides, and validation fails on ASP.NET's
    /// registrations before it ever reaches ours. Going through the real builder means this test
    /// exercises the same graph <c>Program.cs</c> does, which is the only version worth
    /// asserting.</para>
    /// </summary>
    private static IHost Build()
    {
        var builder = WebApplication.CreateBuilder();

        // Both on: a scoped service captured by a singleton, or a dependency nobody registered,
        // fails here rather than on the first request that happens to need it.
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateOnBuild = true;
            options.ValidateScopes = true;
        });

        builder.Services.AddDbContext<StubDb>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        builder.Services.AddScoped<CordangoDbContext>(s => s.GetRequiredService<StubDb>());

        builder.Services.AddCordangoRuntime();
        builder.Services.AddDirectory();
        builder.Services.AddSingleton(AppPermissions.None);

        return builder.Build();
    }

    [Fact]
    public void Every_directory_entity_has_a_working_store()
    {
        using var host = Build();
        using var scope = host.Services.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IRecordStore<Person>>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IRecordStore<Department>>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IRecordStore<Group>>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IRecordStore<Organization>>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IRecordStore<Contact>>());
    }

    /// <summary>An entity with no hooks resolves to a store with no hooks, rather than to a missing
    /// dependency. Most entities have none, so this is the ordinary case and not the edge.</summary>
    [Fact]
    public void An_entity_with_no_hooks_still_resolves()
    {
        using var host = Build();
        using var scope = host.Services.CreateScope();

        var store = scope.ServiceProvider.GetRequiredService<IRecordStore<Person>>();
        Assert.Equal("person", store.Descriptor.EntityKey);
    }

    /// <summary>
    /// Nothing signed in is nobody, and nobody gets nothing.
    ///
    /// <para>The default <see cref="ICurrentUser"/> registration exists so that an application which
    /// has not wired authentication yet fails closed. An application that failed OPEN in that state
    /// would be one where forgetting a line in <c>Program.cs</c> publishes the database.</para>
    /// </summary>
    [Fact]
    public void An_unauthenticated_caller_gets_no_access_even_when_the_definition_declares_no_roles()
    {
        using var host = Build();
        using var scope = host.Services.CreateScope();

        var user = scope.ServiceProvider.GetRequiredService<ICurrentUser>();
        Assert.Null(user.UserId);
        Assert.False(user.IsAdministrator);

        var access = PermissionResolver.Resolve(AppPermissions.None, user, "person");
        Assert.False(access.Read);
        Assert.False(access.Create);
        Assert.False(access.Update);
        Assert.False(access.Delete);
    }

    /// <summary>
    /// Antiforgery is registered globally, on every controller, without anybody opting in.
    ///
    /// <para>Asserted through the filter collection rather than by reading the source, because the
    /// failure this guards against is somebody removing the line while everything still works —
    /// every endpoint keeps answering, for everybody, including a page that forged the request.</para>
    ///
    /// <para><b>What this test cannot tell you</b>, and did not: whether the registered filter can
    /// actually be CONSTRUCTED. The first version used the framework's
    /// <c>AutoValidateAntiforgeryTokenAttribute</c>, which is a factory resolving a service that only
    /// <c>AddControllersWithViews</c> registers — so this assertion passed, startup succeeded, and
    /// every request threw from inside the filter pipeline. Only running the application found it.
    /// The lesson is not to distrust this test but to know its edge: registration is not
    /// resolution.</para>
    /// </summary>
    [Fact]
    public void Every_mutating_request_needs_an_antiforgery_token()
    {
        using var host = Build();
        var provider = host.Services;

        var options = host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Mvc.MvcOptions>>();
        Assert.Contains(options.Value.Filters,
            f => f is Microsoft.AspNetCore.Mvc.Filters.IFilterFactory factory
                && factory.CreateInstance(provider) is AntiforgeryFilter);

        var antiforgery = host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Antiforgery.AntiforgeryOptions>>();
        Assert.Equal(CordangoRuntime.AntiforgeryHeader, antiforgery.Value.HeaderName);
    }

    private sealed class StubDb : CordangoDbContext
    {
        public StubDb(DbContextOptions<StubDb> options, ICurrentUser user, IClock clock)
            : base(options, user, clock) { }

        protected override void ConfigureModel(ModelBuilder builder)
        {
            // The relational mapping the directory configuration applies is meaningless to the
            // in-memory provider, but the entity types themselves have to be in the model or
            // Set<Person>() has nothing to hand back.
            builder.Entity<Person>().HasKey(e => e.Id);
            builder.Entity<Department>().HasKey(e => e.Id);
            builder.Entity<Group>().HasKey(e => e.Id);
            builder.Entity<Organization>().HasKey(e => e.Id);
            builder.Entity<Contact>().HasKey(e => e.Id);
        }
    }
}

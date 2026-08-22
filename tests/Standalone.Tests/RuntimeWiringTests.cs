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

public class RuntimeWiringTests
{
    private static IHost Build()
    {
        var builder = WebApplication.CreateBuilder();

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

    [Fact]
    public void An_entity_with_no_hooks_still_resolves()
    {
        using var host = Build();
        using var scope = host.Services.CreateScope();

        var store = scope.ServiceProvider.GetRequiredService<IRecordStore<Person>>();
        Assert.Equal("person", store.Descriptor.EntityKey);
    }

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
            builder.Entity<Person>().HasKey(e => e.Id);
            builder.Entity<Department>().HasKey(e => e.Id);
            builder.Entity<Group>().HasKey(e => e.Id);
            builder.Entity<Organization>().HasKey(e => e.Id);
            builder.Entity<Contact>().HasKey(e => e.Id);
        }
    }
}

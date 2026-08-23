// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.Standalone.Commands;
using Cordango.Standalone.Data;
using Cordango.Standalone.Hosting;
using Cordango.Standalone.Http;
using Cordango.Standalone.Records;
using Cordango.Standalone.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cordango.Standalone.Directory;

/// <summary>Registering the directory, and the descriptors that let the generic store handle
/// it exactly as it handles a generated entity.</summary>
public static class DirectoryModule
{
    /// <summary>Every directory entity: store, descriptor, hooks. One call in
    /// <c>Program.cs</c>.</summary>
    public static IServiceCollection AddDirectory(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddRecord(PersonDescriptor);
        services.AddRecord(DepartmentDescriptor);
        services.AddRecord(GroupDescriptor);
        services.AddRecord(OrganizationDescriptor);
        services.AddRecord(ContactDescriptor);

        // AddRecord installed the ordinary gateway, which answers from the definition's roles — and
        // the definition has nothing to say about these five. Replacing it here, rather than adding
        // beside it, is what makes IEnumerable<IRecordGateway> hold ONE gateway per entity: two
        // would let a caller reach the directory through whichever came first.
        Replace<Person>(services);
        Replace<Department>(services);
        Replace<Group>(services);
        Replace<Organization>(services);
        Replace<Contact>(services);

        return services;
    }

    /// <summary>
    /// Swap this entity's gateway for the directory's own.
    ///
    /// <para>Only the CLOSED type is removed, and that matters: <c>RecordGateway&lt;Person&gt;</c> is
    /// a different service from <c>RecordGateway&lt;Department&gt;</c>, so this takes out exactly one
    /// registration. Removing <c>IRecordGateway</c> instead would take out every entity's, including
    /// the four registered a line earlier.</para>
    ///
    /// <para>The <c>IRecordGateway</c> registration needs no touching at all: <c>AddRecord</c> wrote
    /// it as a factory that asks for <c>RecordGateway&lt;T&gt;</c>, so it follows this swap.</para>
    /// </summary>
    private static void Replace<T>(IServiceCollection services) where T : class, IRecord, new()
    {
        services.RemoveAll<RecordGateway<T>>();
        services.AddScoped<DirectoryGateway<T>>();
        services.AddScoped(s => (RecordGateway<T>)s.GetRequiredService<DirectoryGateway<T>>());
    }

    public static readonly RecordDescriptor<Person> PersonDescriptor = new("person", "person",
    [
        new("full_name", nameof(Person.FullName), (a, b) => b.FullName = a.FullName),
        new("email", nameof(Person.Email), (a, b) => b.Email = a.Email),
        new("department", nameof(Person.Department), (a, b) => b.Department = a.Department),
        new("manager", nameof(Person.Manager), (a, b) => b.Manager = a.Manager),
        new("location", nameof(Person.Location), (a, b) => b.Location = a.Location),
        new("hire_date", nameof(Person.HireDate), (a, b) => b.HireDate = a.HireDate),
        new("employment_status", nameof(Person.EmploymentStatus), (a, b) => b.EmploymentStatus = a.EmploymentStatus),
        new("has_login", nameof(Person.HasLogin), (a, b) => b.HasLogin = a.HasLogin),
        new("user_id", nameof(Person.UserId), (a, b) => b.UserId = a.UserId),
    ]);

    public static readonly RecordDescriptor<Department> DepartmentDescriptor = new("department", "department",
    [
        new("name", nameof(Department.Name), (a, b) => b.Name = a.Name),
        new("handle", nameof(Department.Handle), (a, b) => b.Handle = a.Handle),
        new("parent", nameof(Department.Parent), (a, b) => b.Parent = a.Parent),
        new("lead", nameof(Department.Lead), (a, b) => b.Lead = a.Lead),
    ]);

    public static readonly RecordDescriptor<Group> GroupDescriptor = new("group", "group",
    [
        new("name", nameof(Group.Name), (a, b) => b.Name = a.Name),
        new("handle", nameof(Group.Handle), (a, b) => b.Handle = a.Handle),
        new("parent", nameof(Group.Parent), (a, b) => b.Parent = a.Parent),
        new("description", nameof(Group.Description), (a, b) => b.Description = a.Description),
        new("group_type", nameof(Group.GroupType), (a, b) => b.GroupType = a.GroupType),
    ]);

    public static readonly RecordDescriptor<Organization> OrganizationDescriptor = new("organization", "organization",
    [
        new("name", nameof(Organization.Name), (a, b) => b.Name = a.Name),
        new("roles", nameof(Organization.Roles), (a, b) => b.Roles = [.. a.Roles]),
        new("status", nameof(Organization.Status), (a, b) => b.Status = a.Status),
        new("industry", nameof(Organization.Industry), (a, b) => b.Industry = a.Industry),
        new("website", nameof(Organization.Website), (a, b) => b.Website = a.Website),
        new("email", nameof(Organization.Email), (a, b) => b.Email = a.Email),
        new("phone", nameof(Organization.Phone), (a, b) => b.Phone = a.Phone),
        new("street", nameof(Organization.Street), (a, b) => b.Street = a.Street),
        new("postcode", nameof(Organization.Postcode), (a, b) => b.Postcode = a.Postcode),
        new("city", nameof(Organization.City), (a, b) => b.City = a.City),
        new("country", nameof(Organization.Country), (a, b) => b.Country = a.Country),
        new("owner", nameof(Organization.Owner), (a, b) => b.Owner = a.Owner),
        new("notes", nameof(Organization.Notes), (a, b) => b.Notes = a.Notes),
    ]);

    public static readonly RecordDescriptor<Contact> ContactDescriptor = new("contact", "contact",
    [
        new("full_name", nameof(Contact.FullName), (a, b) => b.FullName = a.FullName),
        new("organization", nameof(Contact.OrganizationId), (a, b) => b.OrganizationId = a.OrganizationId),
        new("job_title", nameof(Contact.JobTitle), (a, b) => b.JobTitle = a.JobTitle),
        new("email", nameof(Contact.Email), (a, b) => b.Email = a.Email),
        new("phone", nameof(Contact.Phone), (a, b) => b.Phone = a.Phone),
        new("mobile", nameof(Contact.Mobile), (a, b) => b.Mobile = a.Mobile),
        new("is_primary", nameof(Contact.IsPrimary), (a, b) => b.IsPrimary = a.IsPrimary),
        new("status", nameof(Contact.Status), (a, b) => b.Status = a.Status),
        new("owner", nameof(Contact.Owner), (a, b) => b.Owner = a.Owner),
        new("notes", nameof(Contact.Notes), (a, b) => b.Notes = a.Notes),
    ]);
}

/// <summary>
/// The directory's own access rule: **anyone signed in may read it, only an administrator may
/// change it.**
///
/// <para><b>Why it does not go through the definition's roles.</b> The definition never mentions
/// these entities — an application says <c>targetApp: "platform"</c> and points at a directory it
/// assumes exists. Running them through <see cref="PermissionResolver"/> would therefore answer
/// <see cref="EntityAccess.None"/> for every role, and every reference picker in the application
/// would come back empty with a 403 nobody could trace to a rule they wrote.</para>
///
/// <para>The rule chosen instead is the one an organisation chart already implies. Knowing who works
/// here and which team they are on is what makes an approval field usable, and it is not a secret
/// from the people who work here. Editing that chart is administration.</para>
///
/// <para><b>Why it lives on the gateway rather than on the controller.</b> HTTP is not the only way
/// in any more. A rule enforced in a controller would be a rule an MCP client walked straight past,
/// and "the directory is readable but not writable" would silently become "the directory is
/// writable" for anyone holding a token. One place, every face.</para>
/// </summary>
public class DirectoryGateway<T> : RecordGateway<T> where T : class, IRecord, new()
{
    public DirectoryGateway(
        IRecordStore<T> store, AppPermissions permissions, ICurrentUser user, CommandService<T> commands)
        : base(store, permissions, user, commands) { }

    protected override EntityAccess ResolveAccess()
    {
        if (Caller.IsAdministrator) return EntityAccess.Full;
        return Caller.UserId is null ? EntityAccess.None : EntityAccess.ReadOnly;
    }
}

/// <summary>
/// The HTTP face of the directory.
///
/// <para>An application that needs something narrower — a directory only HR may read — registers its
/// own <see cref="DirectoryGateway{T}"/> subclass in place of this one. Doing it there rather than
/// here is what makes the narrower rule apply to every caller rather than only to browsers.</para>
/// </summary>
public abstract class DirectoryController<T> : RecordsController<T> where T : class, IRecord, new()
{
    protected DirectoryController(DirectoryGateway<T> records) : base(records) { }
}

[Route("api/directory/person")]
public sealed class PersonController : DirectoryController<Person>
{
    public PersonController(DirectoryGateway<Person> records) : base(records) { }
}

[Route("api/directory/department")]
public sealed class DepartmentController : DirectoryController<Department>
{
    public DepartmentController(DirectoryGateway<Department> records) : base(records) { }
}

[Route("api/directory/group")]
public sealed class GroupController : DirectoryController<Group>
{
    public GroupController(DirectoryGateway<Group> records) : base(records) { }
}

[Route("api/directory/organization")]
public sealed class OrganizationController : DirectoryController<Organization>
{
    public OrganizationController(DirectoryGateway<Organization> records) : base(records) { }
}

[Route("api/directory/contact")]
public sealed class ContactController : DirectoryController<Contact>
{
    public ContactController(DirectoryGateway<Contact> records) : base(records) { }
}

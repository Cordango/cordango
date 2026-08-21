// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.Standalone.Data;
using Cordango.Standalone.Hosting;
using Cordango.Standalone.Http;
using Cordango.Standalone.Records;
using Cordango.Standalone.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

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
        return services;
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
/// <para>An application that needs something narrower — a directory only HR may read — overrides
/// <see cref="RecordsController{T}.ResolveAccess"/> on its own controller. It is a virtual method
/// on a generated class, which is the point of generating the class.</para>
/// </summary>
public abstract class DirectoryController<T> : RecordsController<T> where T : class, IRecord, new()
{
    protected DirectoryController(IRecordStore<T> store, AppPermissions permissions, ICurrentUser user)
        : base(store, permissions, user) { }

    protected override EntityAccess ResolveAccess()
    {
        if (Caller.IsAdministrator) return EntityAccess.Full;
        return Caller.UserId is null ? EntityAccess.None : EntityAccess.ReadOnly;
    }
}

[Route("api/directory/person")]
public sealed class PersonController : DirectoryController<Person>
{
    public PersonController(IRecordStore<Person> store, AppPermissions permissions, ICurrentUser user)
        : base(store, permissions, user) { }
}

[Route("api/directory/department")]
public sealed class DepartmentController : DirectoryController<Department>
{
    public DepartmentController(IRecordStore<Department> store, AppPermissions permissions, ICurrentUser user)
        : base(store, permissions, user) { }
}

[Route("api/directory/group")]
public sealed class GroupController : DirectoryController<Group>
{
    public GroupController(IRecordStore<Group> store, AppPermissions permissions, ICurrentUser user)
        : base(store, permissions, user) { }
}

[Route("api/directory/organization")]
public sealed class OrganizationController : DirectoryController<Organization>
{
    public OrganizationController(IRecordStore<Organization> store, AppPermissions permissions, ICurrentUser user)
        : base(store, permissions, user) { }
}

[Route("api/directory/contact")]
public sealed class ContactController : DirectoryController<Contact>
{
    public ContactController(IRecordStore<Contact> store, AppPermissions permissions, ICurrentUser user)
        : base(store, permissions, user) { }
}

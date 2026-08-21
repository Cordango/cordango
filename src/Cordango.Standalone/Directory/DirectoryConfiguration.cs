// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.Standalone.Records;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cordango.Standalone.Directory;

/// <summary>
/// How the directory maps to tables.
///
/// <para><b>The <c>directory_</c> prefix is not decoration.</b> An application is free to define its
/// own <c>contact</c> or <c>person</c> entity — plenty do, meaning something quite different by it —
/// and the generated tables are named from the definition's own keys. Without a prefix here the
/// first application to do that would collide with the directory at migration time, and the error
/// would name a table nobody wrote.</para>
///
/// <para>References between directory records are plain string columns holding the target's id,
/// not EF navigation properties. That is the same shape the generated entities use, and for the same
/// reason: the definition's model of a reference is "this field holds that record's id", and
/// inventing an object graph on top of it would make the runtime's idea of a record diverge from the
/// language's.</para>
/// </summary>
public static class DirectoryConfiguration
{
    /// <summary>Apply every directory entity. Called from the generated <c>DbContext</c>, once,
    /// before the application's own entities.</summary>
    public static ModelBuilder AddDirectory(this ModelBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ApplyConfiguration(new PersonConfiguration());
        builder.ApplyConfiguration(new DepartmentConfiguration());
        builder.ApplyConfiguration(new GroupConfiguration());
        builder.ApplyConfiguration(new OrganizationConfiguration());
        builder.ApplyConfiguration(new ContactConfiguration());
        return builder;
    }

    /// <summary>Id column and tracking columns, identical on every directory table. Written once so
    /// that a change to how records are keyed does not have to be remembered five times.</summary>
    internal static void Common<T>(EntityTypeBuilder<T> b, string table)
        where T : class, IRecord, IHasTrackingFields
    {
        b.ToTable(table);
        b.HasKey(e => e.Id);
        b.Property(e => e.Id).HasColumnName("id").HasMaxLength(64);
        b.Property(e => e.Created).HasColumnName("created");
        b.Property(e => e.CreatedBy).HasColumnName("created_by").HasMaxLength(64);
        b.Property(e => e.LastModified).HasColumnName("last_modified");
        b.Property(e => e.LastModifiedBy).HasColumnName("last_modified_by").HasMaxLength(64);
    }
}

internal sealed class PersonConfiguration : IEntityTypeConfiguration<Person>
{
    public void Configure(EntityTypeBuilder<Person> b)
    {
        DirectoryConfiguration.Common(b, "directory_person");
        b.Property(e => e.FullName).HasColumnName("full_name").IsRequired();
        b.Property(e => e.Email).HasColumnName("email");
        b.Property(e => e.Department).HasColumnName("department").HasMaxLength(64);
        b.Property(e => e.Manager).HasColumnName("manager").HasMaxLength(64);
        b.Property(e => e.Location).HasColumnName("location");
        b.Property(e => e.HireDate).HasColumnName("hire_date");
        b.Property(e => e.EmploymentStatus).HasColumnName("employment_status").IsRequired();
        b.Property(e => e.HasLogin).HasColumnName("has_login");
        b.Property(e => e.UserId).HasColumnName("user_id").HasMaxLength(64);

        // "Unique unless absent" needs no filter here: Postgres treats NULLs as distinct in a
        // unique index, so the people with no email address are not all the same person. On SQL
        // Server this line would need one, which is worth knowing before repointing the provider.
        b.HasIndex(e => e.Email).IsUnique();
        b.HasIndex(e => e.FullName);
        b.HasIndex(e => e.Department);
    }
}

internal sealed class DepartmentConfiguration : IEntityTypeConfiguration<Department>
{
    public void Configure(EntityTypeBuilder<Department> b)
    {
        DirectoryConfiguration.Common(b, "directory_department");
        b.Property(e => e.Name).HasColumnName("name").IsRequired();
        b.Property(e => e.Handle).HasColumnName("handle");
        b.Property(e => e.Parent).HasColumnName("parent").HasMaxLength(64);
        b.Property(e => e.Lead).HasColumnName("lead").HasMaxLength(64);
        b.HasIndex(e => e.Name).IsUnique();
    }
}

internal sealed class GroupConfiguration : IEntityTypeConfiguration<Group>
{
    public void Configure(EntityTypeBuilder<Group> b)
    {
        DirectoryConfiguration.Common(b, "directory_group");
        b.Property(e => e.Name).HasColumnName("name").IsRequired();
        b.Property(e => e.Handle).HasColumnName("handle");
        b.Property(e => e.Parent).HasColumnName("parent").HasMaxLength(64);
        b.Property(e => e.Description).HasColumnName("description");
        b.Property(e => e.GroupType).HasColumnName("group_type");
        b.HasIndex(e => e.Name).IsUnique();
    }
}

internal sealed class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
{
    public void Configure(EntityTypeBuilder<Organization> b)
    {
        DirectoryConfiguration.Common(b, "directory_organization");
        b.Property(e => e.Name).HasColumnName("name").IsRequired();
        b.Property(e => e.Roles).HasColumnName("roles");
        b.Property(e => e.Status).HasColumnName("status").IsRequired();
        b.Property(e => e.Industry).HasColumnName("industry");
        b.Property(e => e.Website).HasColumnName("website");
        b.Property(e => e.Email).HasColumnName("email");
        b.Property(e => e.Phone).HasColumnName("phone");
        b.Property(e => e.Street).HasColumnName("street");
        b.Property(e => e.Postcode).HasColumnName("postcode");
        b.Property(e => e.City).HasColumnName("city");
        b.Property(e => e.Country).HasColumnName("country");
        b.Property(e => e.Owner).HasColumnName("owner").HasMaxLength(64);
        b.Property(e => e.Notes).HasColumnName("notes");
        b.HasIndex(e => e.Name);
    }
}

internal sealed class ContactConfiguration : IEntityTypeConfiguration<Contact>
{
    public void Configure(EntityTypeBuilder<Contact> b)
    {
        DirectoryConfiguration.Common(b, "directory_contact");
        b.Property(e => e.FullName).HasColumnName("full_name").IsRequired();
        b.Property(e => e.OrganizationId).HasColumnName("organization").HasMaxLength(64);
        b.Property(e => e.JobTitle).HasColumnName("job_title");
        b.Property(e => e.Email).HasColumnName("email");
        b.Property(e => e.Phone).HasColumnName("phone");
        b.Property(e => e.Mobile).HasColumnName("mobile");
        b.Property(e => e.IsPrimary).HasColumnName("is_primary");
        b.Property(e => e.Status).HasColumnName("status").IsRequired();
        b.Property(e => e.Owner).HasColumnName("owner").HasMaxLength(64);
        b.Property(e => e.Notes).HasColumnName("notes");
        b.HasIndex(e => e.OrganizationId);
    }
}

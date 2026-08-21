// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Text.Json.Serialization;
using Cordango.Standalone.Records;

namespace Cordango.Standalone.Directory;

/// <summary>
/// The five things every business application refers to and none of them define: who works here,
/// how they are organised, and which outside companies and people they deal with.
///
/// <para><b>Why they are in the runtime and not generated.</b> On the platform these live outside
/// any one application — an app says <c>targetApp: "platform"</c> and points at the shared
/// directory, so that the same person is the same person in the helpdesk and in the expense tool.
/// A standalone application has no shared directory to point at, so the directory ships with it. The
/// shapes are lifted from <c>schemas/platform-entities.json</c> and <c>core_organizations</c>, which
/// is what makes a definition written against the platform build here at all: the generator maps
/// <c>platform.person</c> to <see cref="Person"/>, <c>core_organizations.organization</c> to
/// <see cref="Organization"/>, and so on.</para>
///
/// <para><b>What is deliberately missing.</b> The platform's <c>organization</c> carries some sixty
/// <c>enr_*</c> fields — the output of enrichment, which reads the public web and is a platform
/// feature. Shipping the columns without the thing that fills them would put sixty always-empty fields in
/// front of every user. They are absent, and the generator refuses a definition that reaches for
/// them with a message that says so.</para>
/// </summary>
public sealed class Person : IRecord, IHasTrackingFields
{
    public string Id { get; set; } = "";

    [JsonPropertyName("full_name")] public string FullName { get; set; } = "";
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("department")] public string? Department { get; set; }

    /// <summary>The org hierarchy — approvals, delegation and escalation all walk it.</summary>
    [JsonPropertyName("manager")] public string? Manager { get; set; }

    [JsonPropertyName("location")] public string? Location { get; set; }
    [JsonPropertyName("hire_date")] public DateOnly? HireDate { get; set; }
    [JsonPropertyName("employment_status")] public string EmploymentStatus { get; set; } = "active";

    /// <summary>True when this person can sign in. A person record and a login are separate things:
    /// most people in a directory never sign in, and deleting somebody's login should not delete the
    /// approvals they signed.</summary>
    [JsonPropertyName("has_login")] public bool HasLogin { get; set; }

    /// <summary>The Identity user this person signs in as, when they do.</summary>
    [JsonPropertyName("user_id")] public string? UserId { get; set; }

    public DateTimeOffset Created { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? LastModified { get; set; }
    public string? LastModifiedBy { get; set; }
}

/// <summary>A top-level team. Departments and groups are two views over one org structure: every
/// department is a team, and teams nest.</summary>
public sealed class Department : IRecord, IHasTrackingFields
{
    public string Id { get; set; } = "";

    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("handle")] public string? Handle { get; set; }
    [JsonPropertyName("parent")] public string? Parent { get; set; }
    [JsonPropertyName("lead")] public string? Lead { get; set; }

    public DateTimeOffset Created { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? LastModified { get; set; }
    public string? LastModifiedBy { get; set; }
}

/// <summary>Any team, including the ones that are not departments: a project group, a
/// distribution list, an access group.</summary>
public sealed class Group : IRecord, IHasTrackingFields
{
    public string Id { get; set; } = "";

    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("handle")] public string? Handle { get; set; }
    [JsonPropertyName("parent")] public string? Parent { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("group_type")] public string? GroupType { get; set; }

    public DateTimeOffset Created { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? LastModified { get; set; }
    public string? LastModifiedBy { get; set; }
}

/// <summary>An outside company: a customer, a supplier, a partner. <c>Roles</c> is a list because
/// one company is routinely several of those at once.</summary>
public sealed class Organization : IRecord, IHasTrackingFields
{
    public string Id { get; set; } = "";

    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("roles")] public List<string> Roles { get; set; } = [];
    [JsonPropertyName("status")] public string Status { get; set; } = "active";
    [JsonPropertyName("industry")] public string? Industry { get; set; }
    [JsonPropertyName("website")] public string? Website { get; set; }
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("phone")] public string? Phone { get; set; }
    [JsonPropertyName("street")] public string? Street { get; set; }
    [JsonPropertyName("postcode")] public string? Postcode { get; set; }
    [JsonPropertyName("city")] public string? City { get; set; }
    [JsonPropertyName("country")] public string? Country { get; set; }

    /// <summary>The <see cref="Person"/> here who looks after this relationship.</summary>
    [JsonPropertyName("owner")] public string? Owner { get; set; }

    [JsonPropertyName("notes")] public string? Notes { get; set; }

    public DateTimeOffset Created { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? LastModified { get; set; }
    public string? LastModifiedBy { get; set; }
}

/// <summary>A person at an outside company. Kept apart from <see cref="Person"/> deliberately: the
/// two look alike and answer different questions, and merging them is how a directory ends up
/// listing every customer contact as an employee.</summary>
public sealed class Contact : IRecord, IHasTrackingFields
{
    public string Id { get; set; } = "";

    [JsonPropertyName("full_name")] public string FullName { get; set; } = "";
    [JsonPropertyName("organization")] public string? OrganizationId { get; set; }
    [JsonPropertyName("job_title")] public string? JobTitle { get; set; }
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("phone")] public string? Phone { get; set; }
    [JsonPropertyName("mobile")] public string? Mobile { get; set; }
    [JsonPropertyName("is_primary")] public bool IsPrimary { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = "active";
    [JsonPropertyName("owner")] public string? Owner { get; set; }
    [JsonPropertyName("notes")] public string? Notes { get; set; }

    public DateTimeOffset Created { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? LastModified { get; set; }
    public string? LastModifiedBy { get; set; }
}

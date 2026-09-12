// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

import { RecordDescriptor, type RecordRow } from "../records/descriptor.js";

/**
 * The five things every business application refers to and none of them define: who works here,
 * how they are organised, and which outside companies and people they deal with.
 *
 * **Why they are in the runtime and not generated.** On the platform these live outside any one
 * application — an app says `targetApp: "platform"` and points at the shared directory, so that the
 * same person is the same person in the helpdesk and in the expense tool. A standalone application
 * has no shared directory to point at, so the directory ships with it. The shapes are lifted from
 * the platform's own entities, which is what makes a definition written against the platform build
 * here at all: the generator maps `platform.person` to `person`, `core_organizations.organization`
 * to `organization`, and so on.
 *
 * **What is deliberately missing.** The platform's `organization` carries some sixty `enr_*`
 * fields — the output of enrichment, which reads the public web and is a platform feature. Shipping
 * the columns without the thing that fills them would put sixty always-empty fields in front of
 * every user. They are absent, and the generator refuses a definition that reaches for them with a
 * message that says so.
 */
export const directoryEntities = ["person", "department", "group", "organization", "contact"] as const;

export type DirectoryEntity = (typeof directoryEntities)[number];

/** Somebody who works here. */
export const personDescriptor = new RecordDescriptor<RecordRow>("person", "Person", [
  { key: "full_name", type: "text" },
  { key: "email", type: "text" },
  { key: "department", type: "reference" },
  // The org hierarchy — approvals, delegation and escalation all walk it.
  { key: "manager", type: "reference" },
  { key: "location", type: "text" },
  { key: "hire_date", type: "date" },
  { key: "employment_status", type: "text" },
  // True when this person can sign in. A person record and a login are separate things: most people
  // in a directory never sign in, and deleting somebody's login should not delete the approvals
  // they signed.
  { key: "has_login", type: "boolean" },
  // The account this person signs in as, when they do.
  { key: "user_id", type: "text" },
]);

/** A top-level team. Departments and groups are two views over one org structure: every department
 * is a team, and teams nest. */
export const departmentDescriptor = new RecordDescriptor<RecordRow>("department", "Department", [
  { key: "name", type: "text" },
  { key: "handle", type: "text" },
  { key: "parent", type: "reference" },
  { key: "lead", type: "reference" },
]);

/** Any team, including the ones that are not departments: a project group, a distribution list, an
 * access group. */
export const groupDescriptor = new RecordDescriptor<RecordRow>("group", "Group", [
  { key: "name", type: "text" },
  { key: "handle", type: "text" },
  { key: "parent", type: "reference" },
  { key: "description", type: "text" },
  { key: "group_type", type: "text" },
]);

/** An outside company: a customer, a supplier, a partner. `roles` is a list because one company is
 * routinely several of those at once. */
export const organizationDescriptor = new RecordDescriptor<RecordRow>("organization", "Organization", [
  { key: "name", type: "text" },
  { key: "roles", type: "multiselect" },
  { key: "status", type: "text" },
  { key: "industry", type: "text" },
  { key: "website", type: "text" },
  { key: "email", type: "text" },
  { key: "phone", type: "text" },
  { key: "street", type: "text" },
  { key: "postcode", type: "text" },
  { key: "city", type: "text" },
  { key: "country", type: "text" },
  // The person here who looks after this relationship.
  { key: "owner", type: "reference" },
  { key: "notes", type: "text" },
]);

/** A person at an outside company. Kept apart from `person` deliberately: the two look alike and
 * answer different questions, and merging them is how a directory ends up listing every customer
 * contact as an employee. */
export const contactDescriptor = new RecordDescriptor<RecordRow>("contact", "Contact", [
  { key: "full_name", type: "text" },
  { key: "organization", type: "reference" },
  { key: "job_title", type: "text" },
  { key: "email", type: "text" },
  { key: "phone", type: "text" },
  { key: "mobile", type: "text" },
  { key: "is_primary", type: "boolean" },
  { key: "status", type: "text" },
  { key: "owner", type: "reference" },
  { key: "notes", type: "text" },
]);

export const directoryDescriptors: Record<DirectoryEntity, RecordDescriptor<RecordRow>> = {
  person: personDescriptor,
  department: departmentDescriptor,
  group: groupDescriptor,
  organization: organizationDescriptor,
  contact: contactDescriptor,
};

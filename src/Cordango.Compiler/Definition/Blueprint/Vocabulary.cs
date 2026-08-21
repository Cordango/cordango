// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

namespace Cordango.Definition.Blueprints;

/// <summary>
/// The blueprint's closed vocabularies. String constants rather than C# enums, matching
/// <c>JobStatus</c> in the control plane: the values round-trip byte-for-byte with no serializer
/// naming policy, which matters because an approved revision is content-hashed and a hash that moved
/// because of a naming convention would make immutability a lie.
///
/// <para>Every set here is mirrored in <c>schema/blueprint.schema.json</c> and asserted equal by
/// <c>BlueprintVocabularyTests</c>, so the two cannot drift.</para>
/// </summary>
public static class WorkTopologies
{
    public const string Record = "record";
    public const string Queue = "queue";
    public const string Pipeline = "pipeline";
    public const string Matrix = "matrix";
    public const string Calendar = "calendar";
    public const string Dashboard = "dashboard";
    public const string Catalog = "catalog";
    public static readonly string[] All = [Record, Queue, Pipeline, Matrix, Calendar, Dashboard, Catalog];
}

public static class ArchetypeKinds
{
    public const string Tracker = "tracker";
    public const string Pipeline = "pipeline";
    public const string Approval = "approval";
    public const string Scheduling = "scheduling";
    public const string Assignment = "assignment";
    public const string Ticketing = "ticketing";
    public const string Planning = "planning";
    public const string Catalog = "catalog";
    public const string Analytics = "analytics";
    public const string Forms = "forms";
    public static readonly string[] All =
        [Tracker, Pipeline, Approval, Scheduling, Assignment, Ticketing, Planning, Catalog, Analytics, Forms];
}

public static class Behaviors
{
    public const string Stateful = "stateful";
    public const string Approval = "approval";
    public const string Assignment = "assignment";
    public const string Scheduling = "scheduling";
    public const string Collaborative = "collaborative";
    public const string Analytical = "analytical";
    public static readonly string[] All = [Stateful, Approval, Assignment, Scheduling, Collaborative, Analytical];
}

/// <summary>How a concept is realised. The distinction that stops the compiler duplicating shared
/// data as local entities, and stops a plain label set becoming a table nobody wanted.</summary>
public static class ConceptKinds
{
    /// <summary>Its own entity, which the user browses.</summary>
    public const string Local = "local";
    /// <summary>A closed set of values that becomes select options on the referring field. NOT an entity.</summary>
    public const string Enumeration = "enumeration";
    /// <summary>A lookup list the shell groups under Configuration rather than top-level navigation.</summary>
    public const string Config = "config";
    /// <summary>A singleton configuration record, rendered as a grouped form.</summary>
    public const string Settings = "settings";
    /// <summary>Exists only inside a parent record; no page, no navigation, no standalone list.</summary>
    public const string Subordinate = "subordinate";
    /// <summary>Vocabulary only. Labels, no storage at all.</summary>
    public const string Terminology = "terminology";
    public static readonly string[] All = [Local, Enumeration, Config, Settings, Subordinate, Terminology];

    /// <summary>The kinds that produce an entity in the lowered definition.</summary>
    public static bool ProducesEntity(string kind) =>
        kind is Local or Config or Settings or Subordinate;
}

public static class Sensitivities
{
    public const string None = "none";
    public const string Internal = "internal";
    public const string Restricted = "restricted";
    public static readonly string[] All = [None, Internal, Restricted];
}

public static class ExternalSources
{
    public const string Platform = "platform";
    public const string Core = "core";
    public static readonly string[] All = [Platform, Core];
}

public static class Cardinalities
{
    public const string OneToOne = "one_to_one";
    public const string OneToMany = "one_to_many";
    public const string ManyToMany = "many_to_many";
    public static readonly string[] All = [OneToOne, OneToMany, ManyToMany];
}

public static class StorageOwners
{
    public const string From = "from";
    public const string To = "to";
    public static readonly string[] All = [From, To];
}

public static class DeleteBehaviors
{
    public const string Cascade = "cascade";
    public const string Restrict = "restrict";
    public const string SetNull = "set_null";
    public static readonly string[] All = [Cascade, Restrict, SetNull];
}

public static class ChildPresentations
{
    public const string Feed = "feed";
    public const string Checklist = "checklist";
    public const string LineItems = "lineItems";
    public const string Table = "table";
    public static readonly string[] All = [Feed, Checklist, LineItems, Table];
}

/// <summary>Field types a <see cref="FieldSpec"/> may declare. <see cref="Reference"/> is
/// deliberately NOT among <see cref="All"/>: reference fields come from relationships and external
/// references, so every field has exactly one source of truth.</summary>
public static class FieldTypes
{
    public const string Text = "text";
    public const string LongText = "longtext";
    public const string Integer = "integer";
    public const string Decimal = "decimal";
    public const string Money = "money";
    public const string Boolean = "boolean";
    public const string Date = "date";
    public const string DateTime = "datetime";
    public const string Email = "email";
    public const string Url = "url";
    public const string Phone = "phone";
    public const string Select = "select";
    public const string MultiSelect = "multiselect";
    public const string Json = "json";
    public const string Attachment = "attachment";

    /// <summary>Not declarable on a <see cref="FieldSpec"/>; produced by lowering.</summary>
    public const string Reference = "reference";

    public static readonly string[] All =
        [Text, LongText, Integer, Decimal, Money, Boolean, Date, DateTime,
         Email, Url, Phone, Select, MultiSelect, Json, Attachment];

    public static bool IsNumeric(string type) => type is Integer or Decimal or Money;
    public static bool IsTemporal(string type) => type is Date or DateTime;
    public static bool IsChoice(string type) => type is Select or MultiSelect;
}

/// <summary>Why the user needs a field. Grouping by purpose is how the wizard presents fields
/// without asking anyone to design a database.</summary>
public static class FieldPurposes
{
    public const string Identify = "identify";
    public const string Decide = "decide";
    public const string Transition = "transition";
    public const string Report = "report";
    public const string Context = "context";
    public static readonly string[] All = [Identify, Decide, Transition, Report, Context];
}

/// <summary>Where a field came from. A fact about origin, not a model-supplied confidence score:
/// a number a model invents for its own output is not evidence of anything.</summary>
public static class FieldProvenance
{
    public const string ConfirmedByUser = "confirmed_by_user";
    public const string DerivedFromWorkflow = "derived_from_workflow";
    public const string RequiredBySurface = "required_by_surface";
    public const string PlatformDefault = "platform_default";
    public const string ModelSuggested = "model_suggested";
    public static readonly string[] All =
        [ConfirmedByUser, DerivedFromWorkflow, RequiredBySurface, PlatformDefault, ModelSuggested];
}

public static class AggregateOps
{
    public const string Count = "count";
    public const string Sum = "sum";
    public const string Avg = "avg";
    public const string Min = "min";
    public const string Max = "max";
    public static readonly string[] All = [Count, Sum, Avg, Min, Max];

    /// <summary>Every op except <see cref="Count"/> needs a field to aggregate.</summary>
    public static bool NeedsField(string op) => op != Count;
}

public static class Phases
{
    public const string NotStarted = "not_started";
    public const string Active = "active";
    public const string Done = "done";
    public const string Cancelled = "cancelled";
    public static readonly string[] All = [NotStarted, Active, Done, Cancelled];
}

public static class ActionPlacements
{
    public const string Detail = "detail";
    public const string Row = "row";
    public const string Both = "both";
    public static readonly string[] All = [Detail, Row, Both];
}

public static class SurfaceKinds
{
    public const string Table = "table";
    public const string Board = "board";
    public const string Calendar = "calendar";
    public const string Dashboard = "dashboard";
    public const string Detail = "detail";
    public static readonly string[] All = [Table, Board, Calendar, Dashboard, Detail];
}

public static class SurfaceScopes
{
    public const string Global = "global";
    public const string PerRecord = "per_record";
    public static readonly string[] All = [Global, PerRecord];
}

public static class PageRoles
{
    public const string Home = "home";
    public const string Workspace = "workspace";
    public const string Settings = "settings";
    public static readonly string[] All = [Home, Workspace, Settings];
}

public static class FilterOperators
{
    public const string Eq = "eq";
    public const string Neq = "neq";
    public const string Gt = "gt";
    public const string Gte = "gte";
    public const string Lt = "lt";
    public const string Lte = "lte";
    public const string In = "in";
    public const string NotIn = "not_in";
    public const string Contains = "contains";
    public const string IsEmpty = "is_empty";
    public const string IsNotEmpty = "is_not_empty";
    public const string WithinDays = "within_days";
    public const string OlderThanDays = "older_than_days";
    public static readonly string[] All =
        [Eq, Neq, Gt, Gte, Lt, Lte, In, NotIn, Contains, IsEmpty, IsNotEmpty, WithinDays, OlderThanDays];

    /// <summary>Operators that take no value — declaring one is an error, not a harmless extra.</summary>
    public static bool IsValueless(string op) => op is IsEmpty or IsNotEmpty;
}

/// <summary>
/// Values resolved at read time rather than baked in. This is how "assigned to me" becomes something
/// a deterministic compiler can emit.
///
/// <para>Deliberately only what the runtime has a token for — <c>{{actor.id}}</c> and
/// <c>{{today}}</c>. Start-of-week and start-of-month were removed: the clock tokens are offsets
/// (<c>today+7</c>, <c>today-2w</c>) with no start-of-period form, and a blueprint that can express
/// something no lowering rule can produce is a promise the compiler has to break.</para>
/// </summary>
public static class FilterContexts
{
    public const string CurrentActor = "current_actor";
    public const string Today = "today";
    public static readonly string[] All = [CurrentActor, Today];
}

public static class ScenarioOps
{
    public const string Create = "create";
    public const string Update = "update";
    public const string Command = "command";
    public const string Transition = "transition";
    public const string Exists = "exists";
    public const string Delete = "delete";
    public static readonly string[] All = [Create, Update, Command, Transition, Exists, Delete];
}

public static class ScenarioExpectOps
{
    public const string FieldEquals = "field_equals";
    public const string RecordExists = "record_exists";
    public const string RecordAbsent = "record_absent";
    public const string VisibleIn = "visible_in";
    public const string NotVisibleIn = "not_visible_in";
    public const string CountEquals = "count_equals";
    public static readonly string[] All =
        [FieldEquals, RecordExists, RecordAbsent, VisibleIn, NotVisibleIn, CountEquals];
}

public static class Importances
{
    public const string Critical = "critical";
    public const string Important = "important";
    public const string NiceToHave = "nice_to_have";
    public static readonly string[] All = [Critical, Important, NiceToHave];
}

public static class RequirementOrigins
{
    public const string Brief = "brief";
    public const string UserAnswer = "user_answer";
    public const string ModelInference = "model_inference";
    public static readonly string[] All = [Brief, UserAnswer, ModelInference];
}

/// <summary>Computed at lowering: do the requirement's <c>coveredBy</c> references resolve?</summary>
public static class DesignCoverages
{
    public const string Covered = "covered";
    public const string Partial = "partial";
    public const string Uncovered = "uncovered";
    public const string Unsupported = "unsupported";
    public static readonly string[] All = [Covered, Partial, Uncovered, Unsupported];
}

/// <summary>Computed after scenarios run. Separate from <see cref="DesignCoverages"/> because "the
/// structure exists" and "a user could do it" fail independently.</summary>
public static class Verifications
{
    public const string Verified = "verified";
    public const string Failed = "failed";
    public const string NotRun = "not_run";
    public static readonly string[] All = [Verified, Failed, NotRun];
}

public static class SpecLayers
{
    public const string Intent = "intent";
    public const string Actors = "actors";
    public const string Concepts = "concepts";
    public const string Relationships = "relationships";
    public const string Data = "data";
    public const string Workflows = "workflows";
    public const string Jobs = "jobs";
    public const string Experience = "experience";
    public const string Scenarios = "scenarios";
    public static readonly string[] All =
        [Intent, Actors, Concepts, Relationships, Data, Workflows, Jobs, Experience, Scenarios];
}

public static class ResolutionSources
{
    public const string UserAnswer = "user_answer";
    public const string AssumedDefault = "assumed_default";
    public const string Inferred = "inferred";
    public static readonly string[] All = [UserAnswer, AssumedDefault, Inferred];
}

/// <summary>Platform capabilities a blueprint may need and not have. A closed set so the same gap
/// reads identically across every blueprint and can be counted across the corpus, rather than each
/// run inventing its own phrasing for "we cannot do that".</summary>
public static class MissingCapabilities
{
    /// <summary>"Recruiters see only their own candidates". Nothing in the App Definition expresses
    /// row-level ownership, so it is recorded rather than approximated.</summary>
    public const string RowLevelOwnership = "row_level_ownership";
    /// <summary>A record label built from more than one field. <c>displayField</c> takes a single
    /// field key, while computed fields are derived values rather than durable human labels.</summary>
    public const string CompositeRecordLabel = "composite_record_label";
    /// <summary>A reference into an arbitrary other app. The gate skips unknown app keys and the
    /// runtime resolves only known core apps, so this is not supported end to end.</summary>
    public const string CrossAppReference = "cross_app_reference";
    public const string ExternalIntegration = "external_integration";
    public const string ScheduledAutomation = "scheduled_automation";
    public const string Other = "other";
    public static readonly string[] All =
        [RowLevelOwnership, CompositeRecordLabel, CrossAppReference,
         ExternalIntegration, ScheduledAutomation, Other];
}

/// <summary>Id prefixes. The prefix makes a dangling reference obvious on sight and lets the gate
/// tell "this id does not exist" from "this is the wrong KIND of id", which are different mistakes
/// with different fixes.</summary>
public static class IdPrefixes
{
    public const string Concept = "cpt_";
    public const string Field = "fld_";
    public const string Relationship = "rel_";
    public const string External = "ext_";
    public const string Reference = "ref_";
    public const string Workflow = "wfl_";
    public const string State = "sta_";
    public const string Transition = "trn_";
    public const string Action = "act_cmd_";
    public const string Actor = "actor_";
    public const string Job = "job_";
    public const string Surface = "srf_";
    public const string Page = "pg_";
    public const string Metric = "mtr_";
    public const string Scenario = "scn_";
    public const string Uncertainty = "unc_";
}

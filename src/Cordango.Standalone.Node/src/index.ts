// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

export { Dec } from "./calc/decimal.js";
export { Instant, PlainDate } from "./calc/dates.js";
export * as calc from "./calc/calc.js";

export {
  parse,
  hop,
  durationFuncs,
  datePartFuncs,
  dateBoundaryFuncs,
  mathFuncs,
  prevFunc,
  keywords,
  type FieldKind,
  type Kind,
  type Node,
  type ParseResult,
} from "./expr/parse.js";
export {
  evaluate,
  fieldValue,
  recordReader,
  kindOf,
  type EvaluateOptions,
  type FieldValue,
  type Value,
} from "./expr/evaluate.js";

export { readCondition, type Condition } from "./conditions/condition.js";
export {
  evaluateCondition,
  type ConditionRecord,
  type RecordHop,
} from "./conditions/evaluator.js";
export { fillTokens, resolveValue } from "./conditions/value-tokens.js";

export {
  declaresNoRoles,
  noPermissions,
  type AppPermissions,
  type CurrentUser,
  type EntityGrant,
  type FieldOverride,
  type RoleDefinition,
} from "./security/permissions.js";
export { EntityAccess, type FieldRule } from "./security/entity-access.js";
export { resolveAccess, resolveRoles } from "./security/permission-resolver.js";
export { project, rejectedWrites, restricts } from "./security/record-visibility.js";

export { RecordError, type ApiError } from "./records/errors.js";
export {
  RecordDescriptor,
  type FieldType,
  type RecordField,
  type RecordRow,
} from "./records/descriptor.js";
export {
  RecordHooks,
  type AfterCreateHook,
  type AfterDeleteHook,
  type AfterUpdateHook,
  type BeforeCreateHook,
  type BeforeDeleteHook,
  type BeforeUpdateHook,
  type Clock,
  type RecordContext,
} from "./records/hooks.js";
export {
  GuidRecordIdGenerator,
  RecordStore,
  type RecordIdGenerator,
  type RecordStoreApi,
} from "./records/store.js";
export {
  apply as applyQuery,
  narrow,
  parseFilters,
  parseSort,
  type RecordFilter,
  type RecordSort,
} from "./records/query.js";
export {
  aggregate,
  type AggregateBucket,
  type AggregateResult,
} from "./records/aggregate.js";
export {
  RecordGateway,
  maxPageSize,
  type CommandResult,
  type CommandRunner,
  type ListResult,
} from "./records/gateway.js";

export {
  isPgError,
  openDatabase,
  uniqueViolation,
  type SqlDriver,
  type SqlRow,
} from "./db/driver.js";
export { PgliteDriver } from "./db/pglite.js";
export { PostgresDriver } from "./db/postgres.js";
export { columnType, createTableSql, trackingColumns } from "./db/ddl.js";
export { SqlRecordStore } from "./db/sql-store.js";

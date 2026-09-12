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
  computeAll,
  computedField,
  type ComputedOptions,
} from "./records/computed.js";
export {
  RecordGateway,
  maxPageSize,
  projected,
  toWire,
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

// ---- the wire: everything an application serves over HTTP ------------------------------------

export {
  passThroughMessages,
  preferredLanguages,
  JsonApiMessages,
  type ApiMessages,
} from "./http/messages.js";
export { clearCookie, cookie, readCookies, setCookie, type CookieOptions } from "./http/cookies.js";
export {
  caller,
  messagesFor,
  state,
  translate,
  type AuthenticatedAccount,
  type RequestScope,
  type RequestState,
} from "./http/context.js";
export {
  antiforgery,
  antiforgeryCookie,
  antiforgeryHeader,
  issueToken,
  rotate,
  verifyToken,
  type AntiforgeryOptions,
} from "./http/antiforgery.js";
export {
  apiNotFound,
  consoleLog,
  errorHandler,
  handler,
  type ErrorLog,
} from "./http/error-handler.js";
export { requireAdministrator, requireSignIn } from "./http/guards.js";
export { recordsRouter } from "./http/records-router.js";
export { accountRouter, asObject, refuse, text } from "./http/account-router.js";
export { adminRouter } from "./http/admin-router.js";
export { notificationsRouter } from "./http/notifications-router.js";
export { settingsRouter } from "./http/settings-router.js";
export { mediaRouter } from "./http/media-router.js";

// ---- identity --------------------------------------------------------------------------------

export {
  checkPassword,
  hashPassword,
  minimumPasswordLength,
  needsRehash,
  verifyPassword,
} from "./identity/passwords.js";
export {
  administratorRole,
  describeUser,
  isAdministrator,
  isLockedOut,
  UserStore,
  type AppUser,
  type UserSummary,
} from "./identity/users.js";
export {
  readTicket,
  signIn,
  signOut,
  slide,
  type SessionOptions,
  type SessionTicket,
} from "./identity/sessions.js";
export {
  accessKeyPrefix,
  AccessKeyStore,
  mintToken,
  parseToken,
  type AccessKey,
} from "./identity/access-keys.js";

// ---- the built-in directory ------------------------------------------------------------------

export {
  contactDescriptor,
  departmentDescriptor,
  directoryDescriptors,
  directoryEntities,
  groupDescriptor,
  organizationDescriptor,
  personDescriptor,
  type DirectoryEntity,
} from "./directory/entities.js";
export { addDirectory, directoryAccess, directoryRouter } from "./directory/module.js";

// ---- commands, notifications, preferences, media ----------------------------------------------

export {
  CommandCatalogue,
  type CommandDefinition,
  type CommandNotification,
  type CommandSet,
  type EffectDefinition,
} from "./commands/catalogue.js";
export { CommandService, type Actor, type EffectRunner } from "./commands/command-service.js";
export { NotificationService, type Notification } from "./notifications/notifications.js";
export { TableSettingsStore } from "./preferences/table-settings.js";
export { LocalFileStore, readStored, type FileStore, type StoredFile } from "./media/file-store.js";

// ---- the application itself --------------------------------------------------------------------

export {
  anonymous,
  CordangoRuntime,
  hooksFrom,
  toCurrentUser,
  type EntityRegistration,
  type HookSet,
  type RuntimeOptions,
} from "./app/runtime.js";
export { createApp, mountEntities, type AppServerOptions } from "./app/server.js";
export { runSeed } from "./app/seed.js";

// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

import type { NextFunction, Request, RequestHandler, Response } from "express";
import { Instant } from "../calc/dates.js";
import { CommandCatalogue } from "../commands/catalogue.js";
import { CommandService, type EffectRunner } from "../commands/command-service.js";
import type { SqlDriver } from "../db/driver.js";
import { SqlRecordStore } from "../db/sql-store.js";
import type { RequestScope, RequestState } from "../http/context.js";
import type { ApiMessages } from "../http/messages.js";
import { passThroughMessages } from "../http/messages.js";
import { AccessKeyStore } from "../identity/access-keys.js";
import { readTicket, slide, type SessionOptions } from "../identity/sessions.js";
import { administratorRole, UserStore, type AppUser } from "../identity/users.js";
import { NotificationService } from "../notifications/notifications.js";
import type { RecordDescriptor, RecordRow } from "../records/descriptor.js";
import { RecordGateway, type CommandRunner } from "../records/gateway.js";
import { RecordHooks, type Clock, type RecordContext } from "../records/hooks.js";
import type { RecordIdGenerator, RecordStoreApi } from "../records/store.js";
import { noPermissions, type AppPermissions, type CurrentUser } from "../security/permissions.js";
import type { EntityAccess } from "../security/entity-access.js";
import { TableSettingsStore } from "../preferences/table-settings.js";

/**
 * The hooks one entity carries, as the generated application declares them.
 *
 * A factory rather than a fixed list, because a hook that maintains a total has to reach the
 * entity holding it — and which rows it may reach is a fact about the caller, not about the
 * application. The scope it is handed is the same one the request is using, so a cascade started
 * by a write reads the rows that write has already made.
 */
export type HookSet<T extends RecordRow> = {
  beforeCreate?: ((record: T, context: RecordContext) => Promise<void> | void)[];
  afterCreate?: ((record: T, context: RecordContext) => Promise<void> | void)[];
  beforeUpdate?: ((record: T, before: T, context: RecordContext) => Promise<void> | void)[];
  afterUpdate?: ((record: T, before: T, context: RecordContext) => Promise<void> | void)[];
  beforeDelete?: ((record: T, context: RecordContext) => Promise<void> | void)[];
  afterDelete?: ((record: T, context: RecordContext) => Promise<void> | void)[];
};

/** One entity, as the generated application registers it. */
export interface EntityRegistration<T extends RecordRow = RecordRow> {
  readonly descriptor: RecordDescriptor<T>;
  /** Stamp `created_at`/`created_by`/`updated_at`/`updated_by`. On unless the entity is one of the
   * runtime's own tables, which keep their own columns. */
  readonly tracking?: boolean;
  readonly hooks?: (scope: RequestScope) => HookSet<T>;
  /**
   * An access rule of this entity's own, instead of the definition's roles.
   *
   * The directory needs one: the definition never mentions `person` or `organization` — an
   * application says `targetApp: "platform"` and points at a directory it assumes exists — so
   * running them through the permission resolver would answer "nothing" for every role, and every
   * reference picker in the application would come back empty with a 403 nobody could trace to a
   * rule they wrote.
   *
   * It lives HERE, on the registration, rather than on a route. HTTP is not the only way in: a rule
   * enforced in a route is a rule an MCP client walks straight past, and "the directory is readable
   * but not writable" would silently become "the directory is writable" for anyone holding a token.
   */
  readonly access?: (user: CurrentUser) => EntityAccess;
}

export interface RuntimeOptions {
  /** The slug: cookie name, container name, database name. */
  readonly appKey: string;
  readonly appName: string;
  readonly driver: SqlDriver;
  /** The definition's roles, compiled in. */
  readonly permissions?: AppPermissions;
  readonly commands?: CommandCatalogue;
  /**
   * The key every cookie in this application is signed under.
   *
   * There is no default and there cannot be one: a generated application shipping with a known
   * signing key is a generated application anybody can forge a session for. The host reads it from
   * the environment and refuses to start without it.
   */
  readonly secret: string;
  /** `Secure` on cookies. Off on a plain-HTTP dev origin, where the browser stores nothing and the
   * symptom is a login that appears to succeed and leaves you signed out. */
  readonly secure?: boolean;
  readonly messages?: ApiMessages;
  readonly clock?: Clock;
  readonly ids?: RecordIdGenerator;
  /** Runs a command's effects. Absent on an application whose definition declares none. */
  readonly effects?: (scope: RequestScope) => EffectRunner;
}

/** Nobody. A real answer, not a missing one. */
export const anonymous: CurrentUser = {
  userId: null,
  isAdministrator: false,
  roleKeys: [],
  personId: null,
};

const systemClock: Clock = {
  get utcNow(): Instant {
    // Milliseconds from the platform clock, widened to the microseconds an Instant holds. Node has
    // no microsecond wall clock, so the last three digits are honestly zero rather than invented.
    return new Instant(BigInt(Date.now()) * 1000n);
  },
};

/**
 * Everything a generated application is, before its routes are mounted.
 *
 * **What it owns:** the database, the entity registry, the identity tables, and the per-request
 * resolution of who is asking. **What it deliberately does not own:** routes. The routers are
 * separate and take a runtime, so a generated application can mount the ones it wants, add its own
 * beside them, and put a middleware in front of any of it.
 */
export class CordangoRuntime {
  private readonly registrations = new Map<string, EntityRegistration<never>>();
  private readonly options: RuntimeOptions;
  private started = false;

  readonly users: UserStore;
  readonly accessKeys: AccessKeyStore;
  readonly notifications: NotificationService;
  readonly tableSettings: TableSettingsStore;
  readonly commands: CommandCatalogue;
  readonly permissions: AppPermissions;
  readonly messages: ApiMessages;
  readonly clock: Clock;

  constructor(options: RuntimeOptions) {
    if (options.secret === undefined || options.secret === "")
      throw new Error(
        "CordangoRuntime needs a signing secret. Set APP_SECRET in the environment — an "
          + "application with a known signing key is an application anybody can forge a session for.",
      );

    this.options = options;
    this.users = new UserStore(options.driver);
    this.accessKeys = new AccessKeyStore(options.driver);
    this.notifications = new NotificationService(options.driver);
    this.tableSettings = new TableSettingsStore(options.driver);
    this.commands = options.commands ?? CommandCatalogue.empty;
    this.permissions = options.permissions ?? noPermissions;
    this.messages = options.messages ?? passThroughMessages;
    this.clock = options.clock ?? systemClock;
  }

  get appKey(): string {
    return this.options.appKey;
  }

  get appName(): string {
    return this.options.appName;
  }

  get driver(): SqlDriver {
    return this.options.driver;
  }

  get secure(): boolean {
    return this.options.secure ?? false;
  }

  /** Every entity key registered, in registration order — which is definition order, because the
   * generated setup file lists them the way the definition does. */
  get entities(): string[] {
    return [...this.registrations.keys()];
  }

  descriptorFor(entity: string): RecordDescriptor<RecordRow> | undefined {
    return this.registrations.get(entity)?.descriptor as RecordDescriptor<RecordRow> | undefined;
  }

  /** How the session cookie is named and signed. */
  get sessionOptions(): SessionOptions {
    return {
      cookieName: `${this.options.appKey}-session`,
      secret: this.options.secret,
      secure: this.secure,
    };
  }

  register<T extends RecordRow>(registration: EntityRegistration<T>): this {
    this.registrations.set(
      registration.descriptor.entityKey,
      registration as unknown as EntityRegistration<never>,
    );
    return this;
  }

  /**
   * Create every table this application needs.
   *
   * The runtime owns its own schema: the first build emits no migration files, so the tables are
   * created from the descriptors on the way up. `CREATE TABLE IF NOT EXISTS` throughout, so a
   * second start over an existing database changes nothing.
   */
  async start(): Promise<void> {
    if (this.started) return;

    await this.users.ensureTables();
    await this.accessKeys.ensureTables();
    await this.notifications.ensureTables();
    await this.tableSettings.ensureTables();

    const scope = this.scopeFor(anonymous);
    for (const entity of this.registrations.keys()) {
      const store = scope.store(entity);
      if (store instanceof SqlRecordStore) await store.ensureTable();
    }

    this.started = true;
  }

  /**
   * Everything one request may reach, bound to one caller and one clock.
   *
   * Built per request rather than shared, because the stores it holds stamp rows with that
   * caller's id and that request's `now`. Reused WITHIN a request, so a hook that fires halfway
   * through a write reads the same clock as the stamp on the row.
   */
  scopeFor(user: CurrentUser): RequestScope {
    const stores = new Map<string, RecordStoreApi<RecordRow>>();
    const gateways = new Map<string, RecordGateway<RecordRow>>();
    const clock = this.clock;
    const runtime = this;

    const scope: RequestScope = {
      user,
      clock,
      entities: this.entities,

      store<T extends RecordRow>(entity: string): RecordStoreApi<T> {
        const existing = stores.get(entity);
        if (existing !== undefined) return existing as unknown as RecordStoreApi<T>;

        const registration = runtime.registrations.get(entity);
        if (registration === undefined)
          throw new Error(
            `This application has no entity '${entity}'. Registered: `
              + `${runtime.entities.join(", ") || "(none)"}.`,
          );

        const descriptor = registration.descriptor as unknown as RecordDescriptor<T>;
        const hooks = registration.hooks as unknown as
          | ((scope: RequestScope) => HookSet<T>)
          | undefined;

        const store = new SqlRecordStore<T>(
          descriptor,
          hooksFrom(hooks === undefined ? {} : hooks(scope)),
          user,
          clock,
          runtime.options.driver,
          runtime.options.ids,
          registration.tracking ?? true,
        );

        stores.set(entity, store as unknown as RecordStoreApi<RecordRow>);
        return store;
      },

      gateway<T extends RecordRow>(entity: string): RecordGateway<T> {
        const existing = gateways.get(entity);
        if (existing !== undefined) return existing as unknown as RecordGateway<T>;

        const store = scope.store<T>(entity);
        const commands: CommandRunner<T> = new CommandService<T>(
          store,
          runtime.commands,
          { userId: user.userId, personId: user.personId ?? null },
          clock,
          runtime.notifications,
          runtime.options.effects === undefined ? null : runtime.options.effects(scope),
        );

        const registration = runtime.registrations.get(entity);
        const rule = registration?.access as ((user: CurrentUser) => EntityAccess) | undefined;

        const gateway = rule === undefined
          ? new RecordGateway<T>(store, runtime.permissions, user, commands)
          : new RuledGateway<T>(store, runtime.permissions, user, commands, rule);

        gateways.set(entity, gateway as unknown as RecordGateway<RecordRow>);
        return gateway;
      },
    };

    return scope;
  }

  /**
   * Tie a login to a directory Person, creating the Person when there is none.
   *
   * **Every account needs this, not only the first one.** The definition's own screens filter on
   * the PERSON — "tickets assigned to me", "expenses I submitted" — so somebody with a login and no
   * person record signs in perfectly and finds every one of those screens empty, with nothing on
   * the page to suggest why. It is also what `{{actor.id}}` resolves to, so a command that stamps
   * an approver would stamp nothing.
   *
   * `personId` names an EXISTING person to attach to, which is the usual case once a directory has
   * people in it: the person is already there, and creating a second record for the same human is
   * how a directory stops being one. Without it the person is found by email address, or created.
   */
  async linkPerson(userId: string, email: string, personId?: string | null): Promise<string | null> {
    const scope = this.scopeFor(anonymous);
    const people = scope.store<RecordRow>("person");
    const rows = await people.query();

    const chosen = personId !== undefined && personId !== null && personId !== ""
      ? rows.find((row) => row.id === personId)
      : rows.find((row) => typeof row["email"] === "string"
          && (row["email"] as string).toLowerCase() === email.toLowerCase());

    let id: string;

    if (chosen === undefined) {
      const user = await this.users.findById(userId);
      const displayName = user === null || user.displayName === "" ? email : user.displayName;

      // A readable id while one is free — "person-admin" in a foreign key column is worth something
      // when you are reading rows by hand. A second administrator created later must not collide
      // with it.
      id = rows.some((row) => row.id === "person-admin") ? `person-${userId}` : "person-admin";

      await people.create({
        id,
        full_name: displayName,
        email,
        department: null,
        manager: null,
        location: null,
        hire_date: null,
        employment_status: "active",
        has_login: true,
        user_id: userId,
      });
    } else {
      id = chosen.id;
      await people.update(id, { ...chosen, has_login: true, user_id: userId }, [
        "has_login",
        "user_id",
      ]);
    }

    await this.users.updateProfile(userId, { personId: id });
    return id;
  }

  /**
   * Release the directory Person a login is being deleted from.
   *
   * The PERSON stays, along with everything it created and approved — a leaver who takes their
   * signatures with them is a hole in the history, not a tidy-up. What goes is the link and the
   * flag that says they can sign in.
   */
  async unlinkPerson(userId: string): Promise<void> {
    const people = this.scopeFor(anonymous).store<RecordRow>("person");
    const linked = (await people.query()).find((row) => row["user_id"] === userId);
    if (linked === undefined) return;

    await people.update(linked.id, { ...linked, has_login: false, user_id: null }, [
      "has_login",
      "user_id",
    ]);
  }

  /**
   * An administrator from the environment, for a deployment whose first visitor will not be you.
   *
   * **Nobody is ever handed a password they did not pick.** There were two other ways to get an
   * administrator onto an empty database and both are worse. A fixed default would mean every
   * application this toolchain produces ships with the same credentials. A GENERATED one has no
   * such hole, but it can only be delivered by printing it to the log — and a first run that ends
   * with "now go and find the password in the container output" is a first run most people fail.
   *
   * So: this creates an account only when somebody chose its password, and does nothing at all
   * otherwise, leaving the first-run screen to ask for one. Doing nothing is also the answer once
   * an administrator exists, so setting these variables permanently is safe.
   */
  async ensureAdministrator(
    email: string | null | undefined,
    password: string | null | undefined,
  ): Promise<boolean> {
    if (email === null || email === undefined || email === "") return false;
    if (password === null || password === undefined || password === "") return false;
    if (await this.users.administratorExists()) return false;

    const created = await this.users.create({
      email,
      password,
      displayName: email,
      roles: [administratorRole],
    });

    // A login and a directory Person are separate things, and this account needs both — see
    // linkPerson for what an administrator with no person record actually looks like.
    await this.linkPerson(created.id, email);

    return true;
  }

  /**
   * Work out who is asking, and hang the request's scope off it.
   *
   * **Two credentials, one answer.** A browser presents a session cookie; a script, a CI job and an
   * MCP client present a bearer access key. Both resolve to the same account with the same roles,
   * so both reach exactly the same rows — rather than a second authorisation path that drifts from
   * the first.
   *
   * An unrecognised credential is not an error here. It is anonymity, and the routes that need
   * somebody signed in say so themselves. Refusing at this layer would make every anonymous route —
   * the login form, the first-run check, a published form — impossible to reach.
   */
  middleware(): RequestHandler {
    return (request: Request, response: Response, next: NextFunction): void => {
      void this.authenticate(request, response)
        .then(({ user, account }) => {
          const state: RequestState = {
            scope: this.scopeFor(user),
            messages: this.messages,
            user,
            account,
          };
          request.cordango = state;
          next();
        })
        .catch(next);
    };
  }

  private async authenticate(
    request: Request,
    response: Response,
  ): Promise<{ user: CurrentUser; account: RequestState["account"] }> {
    const bearer = bearerToken(request);
    if (bearer !== null) {
      const userId = await this.accessKeys.verify(bearer);
      if (userId === null) return { user: anonymous, account: null };

      const user = await this.users.findById(userId);
      // A key belonging to an account that has since been locked or deleted is not a credential.
      if (user === null || isShutOut(user)) return { user: anonymous, account: null };

      return { user: toCurrentUser(user), account: describe(user) };
    }

    const ticket = readTicket(request, this.sessionOptions);
    if (ticket === null) return { user: anonymous, account: null };

    const user = await this.users.findById(ticket.userId);
    if (user === null || isShutOut(user)) return { user: anonymous, account: null };

    // The stamp is the revocation. A cookie minted before a password change, a reset, a lockout or
    // a role change carries the old stamp and stops being a session here — on every device at once,
    // with no table to sweep.
    if (user.securityStamp !== ticket.stamp) return { user: anonymous, account: null };

    slide(response, this.sessionOptions, ticket);
    return { user: toCurrentUser(user), account: describe(user) };
  }
}

/** The bearer token this request presents, or null. */
function bearerToken(request: Request): string | null {
  const header = request.headers.authorization;
  if (header === undefined) return null;
  if (!header.toLowerCase().startsWith("bearer ")) return null;

  const token = header.slice("bearer ".length).trim();
  return token === "" ? null : token;
}

function isShutOut(user: AppUser): boolean {
  return user.lockoutEnd !== null && user.lockoutEnd.getTime() > Date.now();
}

/** The account as a permission identity. The administrator role is the runtime's own bypass and is
 * deliberately not passed on as one of the definition's roles. */
export function toCurrentUser(user: AppUser): CurrentUser {
  return {
    userId: user.id,
    isAdministrator: user.roles.includes(administratorRole),
    roleKeys: user.roles.filter((role) => role !== administratorRole),
    personId: user.personId,
  };
}

function describe(user: AppUser): RequestState["account"] {
  return {
    id: user.id,
    email: user.email,
    displayName: user.displayName === "" ? user.email : user.displayName,
    personId: user.personId,
    roles: [...user.roles].sort(),
    isAdministrator: user.roles.includes(administratorRole),
    mustChangePassword: user.mustChangePassword,
  };
}

/** A gateway whose access comes from the registration rather than from the definition's roles. The
 * one override point the runtime has, and the reason it is an override rather than a branch inside
 * the gateway: an application that needs a narrower directory subclasses this. */
class RuledGateway<T extends RecordRow> extends RecordGateway<T> {
  constructor(
    store: RecordStoreApi<T>,
    permissions: AppPermissions,
    user: CurrentUser,
    commands: CommandRunner<T>,
    private readonly rule: (user: CurrentUser) => EntityAccess,
  ) {
    super(store, permissions, user, commands);
  }

  protected override resolveAccess(): EntityAccess {
    return this.rule(this.caller);
  }
}

/** A declared hook set, as the pipeline wants it. */
export function hooksFrom<T extends RecordRow>(set: HookSet<T>): RecordHooks<T> {
  return new RecordHooks<T>(
    set.beforeCreate ?? [],
    set.afterCreate ?? [],
    set.beforeUpdate ?? [],
    set.afterUpdate ?? [],
    set.beforeDelete ?? [],
    set.afterDelete ?? [],
  );
}

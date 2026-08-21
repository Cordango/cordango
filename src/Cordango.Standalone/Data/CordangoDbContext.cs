// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.Standalone.Records;
using Microsoft.EntityFrameworkCore;

namespace Cordango.Standalone.Data;

/// <summary>
/// The base every generated application's <c>DbContext</c> derives from.
///
/// <para><b>What it does not do: find things.</b> The pattern this follows normally pairs a generic
/// context with <c>ApplyConfigurationsFromAssembly</c> and a reflection sweep for unmapped types, so
/// that dropping a class into the project is enough to get a table. That is the right trade for a
/// framework, which meets its entities at startup. A generator meets them at build time, and its
/// derived context simply lists them:</para>
///
/// <code>
/// protected override void ConfigureModel(ModelBuilder builder)
/// {
///     builder.ApplyConfiguration(new ExpenseConfiguration());
///     builder.ApplyConfiguration(new ExpenseLineConfiguration());
/// }
/// </code>
///
/// <para>The seam is still EF's own <see cref="IEntityTypeConfiguration{TEntity}"/> — one generated
/// file per entity, which is the good idea worth keeping. Only the discovery goes, and what replaces
/// it is a file you can read to learn what this application stores, in an order that does not depend
/// on how the assembly happened to be laid out.</para>
/// </summary>
public abstract class CordangoDbContext : DbContext
{
    private readonly ICurrentUser _user;
    private readonly IClock _clock;

    protected CordangoDbContext(DbContextOptions options, ICurrentUser user, IClock clock)
        : base(options)
    {
        _user = user;
        _clock = clock;
    }

    /// <summary>Apply one <see cref="IEntityTypeConfiguration{TEntity}"/> per entity, in the order
    /// the definition lists them. Generated.</summary>
    protected abstract void ConfigureModel(ModelBuilder builder);

    protected sealed override void OnModelCreating(ModelBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        base.OnModelCreating(builder);
        ConfigureModel(builder);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampTracking();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampTracking();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Who wrote this row and when, stamped in the single place every write passes through.
    ///
    /// <para>Doing it here rather than in each write path is the whole reason
    /// <see cref="IHasTrackingFields"/> exists as a type: <c>Entries&lt;IHasTrackingFields&gt;()</c>
    /// asks the change tracker for the entities that implement it, and there is no way to write that
    /// question backwards and still compile. The prior art wrote the equivalent check by hand at six
    /// call sites, with the operands reversed at every one, and shipped applications whose audit
    /// columns were silently always null.</para>
    ///
    /// <para><c>Created</c> is stamped only on insert and <c>LastModified</c> only on update, so a
    /// row's creation stamp survives every later edit. <c>CreatedBy</c> may be null: seed runs and
    /// scheduled work have no user, and inventing one would make the audit trail lie. Neither is
    /// overwritten when the row already carries one — nothing a CLIENT sends can reach these, since
    /// they are not in any entity's descriptor, so a value that is already there was put there
    /// deliberately.</para>
    /// </summary>
    private void StampTracking()
    {
        var now = _clock.UtcNow;
        var by = _user.UserId;

        foreach (var entry in ChangeTracker.Entries<IHasTrackingFields>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    // Only when nothing else said. The runtime PROVIDES these values; it does not
                    // insist on them. A seed run carries its own dates — a dataset where every row
                    // was created at the same instant is one where "recently created" means nothing
                    // and a chart over creation dates is a single bar.
                    if (entry.Entity.Created == default) entry.Entity.Created = now;
                    entry.Entity.CreatedBy ??= by;
                    break;
                case EntityState.Modified:
                    entry.Entity.LastModified = now;
                    entry.Entity.LastModifiedBy = by;
                    // Whatever the client sent for these, the row keeps the ones it was born with.
                    entry.Property(e => e.Created).IsModified = false;
                    entry.Property(e => e.CreatedBy).IsModified = false;
                    break;
            }
        }
    }
}

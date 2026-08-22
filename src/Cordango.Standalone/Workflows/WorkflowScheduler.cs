// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using Cordango.Standalone.Records;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cordango.Standalone.Workflows;

/// <summary>
/// The workflows that run on a clock rather than on a write.
///
/// <para>Wakes once a minute, asks each scheduled workflow whether this is one of its minutes, and
/// for the ones that say yes runs the workflow against every record of its entity — the condition
/// deciding which records it actually applies to. "Remind the owner of a deal untouched for seven
/// days" is a schedule plus a condition, and neither half means anything alone.</para>
///
/// <para><b>Every tick gets its own scope.</b> A background service is a singleton and the runtime is
/// scoped: the DbContext, the workflow depth and the writers all belong to one unit of work. Sharing
/// them across ticks is the classic way to end up with a change tracker that has been accumulating
/// entities since the process started.</para>
///
/// <para><b>A tick that throws does not stop the scheduler.</b> A background service whose loop
/// escapes is a service that is silently dead for the life of the process, and nothing about the
/// application looks wrong afterwards.</para>
/// </summary>
public sealed class WorkflowScheduler : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly AppWorkflowCatalogue _catalogue;
    private readonly ILogger<WorkflowScheduler> _log;

    public WorkflowScheduler(
        IServiceScopeFactory scopes,
        AppWorkflowCatalogue catalogue,
        ILogger<WorkflowScheduler> log)
    {
        _scopes = scopes;
        _catalogue = catalogue;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var scheduled = _catalogue.Workflows
            .Where(w => string.Equals(w.Event, WorkflowEvent.Schedule, StringComparison.Ordinal))
            .ToList();

        // Nothing to do, ever. Returning rather than looping means an application with no schedules
        // does not wake a thread every minute for the rest of its life.
        if (scheduled.Count == 0) return;

        _log.LogInformation("{Count} scheduled workflow(s). Times are UTC.", scheduled.Count);

        // The minute already handled, so a tick that arrives twice in the same minute — a slow
        // previous run, a clock nudged by NTP — does not run everything twice.
        var lastMinute = DateTimeOffset.MinValue;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                using var scope = _scopes.CreateScope();
                var clock = scope.ServiceProvider.GetRequiredService<IClock>();

                var minute = Truncate(clock.UtcNow);
                if (minute <= lastMinute) continue;
                lastMinute = minute;

                var due = scheduled.Where(w => CronSchedule.Matches(w.Cron, minute)).ToList();
                if (due.Count == 0) continue;

                await RunAsync(scope.ServiceProvider, due, stoppingToken);
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                // Logged and swallowed. See the class summary: an escaped exception here is a
                // scheduler that is dead for the life of the process with nothing to show for it.
                _log.LogError(failure, "A scheduled workflow tick failed. The scheduler is still running.");
            }
        }
    }

    private async Task RunAsync(
        IServiceProvider services, IReadOnlyList<WorkflowDefinition> due, CancellationToken ct)
    {
        var runner = services.GetRequiredService<WorkflowRunner>();
        var writers = services.GetServices<IEntityWriter>().ToList();

        foreach (var workflow in due)
        {
            var writer = writers.FirstOrDefault(w =>
                string.Equals(w.Entity, workflow.Entity, StringComparison.Ordinal));

            if (writer is null)
            {
                _log.LogError(
                    "Scheduled workflow '{Workflow}' names entity '{Entity}', which this application does not have.",
                    workflow.Key, workflow.Entity);
                continue;
            }

            var records = await writer.WhereAsync([], ct);

            _log.LogInformation(
                "Scheduled workflow '{Workflow}' over {Count} {Entity} record(s).",
                workflow.Key, records.Count, workflow.Entity);

            // Through the ordinary dispatch, so a scheduled workflow's condition, effects, cycle
            // guard and logging are the same ones a written-triggered workflow gets. Only the reason
            // it ran is different.
            foreach (var record in records)
                await runner.ScheduledAsync(workflow, record, ct);
        }
    }

    /// <summary>The start of the minute. Cron's resolution is a minute, so comparing anything finer
    /// would make "already handled" depend on where in the second the tick landed.</summary>
    private static DateTimeOffset Truncate(DateTimeOffset at) =>
        new(at.Year, at.Month, at.Day, at.Hour, at.Minute, 0, at.Offset);
}

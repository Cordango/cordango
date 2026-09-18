// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Cordango and contributors.
// Part of Cordango, the open application language and compiler: https://github.com/cordango/cordango
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Globalization;
using Cordango.Standalone.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Cordango.Standalone.Calendar;

public static class CalendarModule
{
    /// <summary>
    /// Wire the personal calendar up, when any entity opts into one.
    ///
    /// <para>Called by generated code with the descriptor the generator resolved. An application
    /// where nothing carries a <c>calendar</c> flag never calls it, and the controller then answers
    /// 404 like any other address that is not there — gated on the descriptor being registered
    /// rather than on a flag somebody could set inconsistently, the same as
    /// <see cref="Forms.FormsModule.AddForms"/>.</para>
    /// </summary>
    public static IServiceCollection AddAppCalendar(
        this IServiceCollection services, CalendarDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(descriptor);

        services.AddSingleton(descriptor);
        services.AddScoped<CalendarService>();
        return services;
    }
}

/// <summary>
/// Your dates, from every entity in this application that has any.
///
/// <para><b>There is no "whose" parameter and there never will be.</b> The person is the signed-in
/// one, resolved server-side. An endpoint called "my calendar" that took an id would be a way to
/// read anybody's week, and the response would look entirely normal to whoever reviewed it.</para>
/// </summary>
[ApiController]
[Authorize]
[Route("api/me/calendar")]
public sealed class MyCalendarController : ControllerBase
{
    /// <summary>How wide a window one request may ask for. A year of one person's dates is a
    /// reasonable thing to want; a decade is somebody reading the table a page at a time.</summary>
    private const int MaxWindowDays = 400;

    private readonly CalendarService _calendar;
    private readonly CalendarDescriptor _descriptor;

    public MyCalendarController(CalendarService calendar, CalendarDescriptor descriptor)
    {
        _calendar = calendar;
        _descriptor = descriptor;
    }

    /// <param name="from">Inclusive, <c>YYYY-MM-DD</c>. Defaults to the start of this month.</param>
    /// <param name="until">EXCLUSIVE, <c>YYYY-MM-DD</c>. Defaults to six weeks after
    /// <paramref name="from"/> — a month grid, which is what asks for this.</param>
    [HttpGet]
    public async Task<IActionResult> Window(
        [FromQuery] string? from = null,
        [FromQuery] string? until = null,
        CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        if (!TryDate(from, new DateOnly(today.Year, today.Month, 1), out var start))
            return BadRequest(this.Refuse("calendar.window_invalid", "'from' is not a date."));

        if (!TryDate(until, start.AddDays(42), out var stop))
            return BadRequest(this.Refuse("calendar.window_invalid", "'until' is not a date."));

        if (stop <= start)
            return BadRequest(this.Refuse(
                "calendar.window_invalid", "'until' has to be after 'from'."));

        if (stop.DayNumber - start.DayNumber > MaxWindowDays)
            return BadRequest(this.Refuse(
                "calendar.window_too_wide",
                $"A calendar window may span {MaxWindowDays} days. Ask for a narrower one."));

        var window = await _calendar.ReadAsync(Iso(start), Iso(stop), ct);

        // Refused rather than truncated: see CalendarService.MaxEntries. 413 rather than 400 because
        // nothing about the REQUEST is wrong — it is the answer that does not fit.
        if (window.Truncated)
            return StatusCode(StatusCodes.Status413PayloadTooLarge, this.Refuse(
                "calendar.too_many",
                $"That window holds more than {CalendarService.MaxEntries} entries. Ask for a "
                + "narrower one — a calendar that dropped the rest would look like a free day."));

        return Ok(new
        {
            from = Iso(start),
            until = Iso(stop),
            entries = window.Entries,
            // What this application contributes at all, so a client can say "nothing here" without
            // having to guess whether it asked wrongly.
            entities = _descriptor.Sources.Select(s => s.Entity),
        });
    }

    private static bool TryDate(string? given, DateOnly fallback, out DateOnly date)
    {
        if (string.IsNullOrWhiteSpace(given))
        {
            date = fallback;
            return true;
        }

        return DateOnly.TryParse(given, CultureInfo.InvariantCulture, out date);
    }

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

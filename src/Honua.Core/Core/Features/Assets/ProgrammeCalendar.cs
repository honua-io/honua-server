// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Assets;

public enum ProgrammeIntervalUnit
{
    Day,
    Week,
    Month,
    Year
}

public sealed record ProgrammeScheduledCycle(int Index, DateTimeOffset DueAt);

public sealed record ProgrammeInterval(ProgrammeIntervalUnit Unit, int Count = 1);

public sealed record ProgrammeCalendarWindow(DateTimeOffset Through, DateTimeOffset? EndAt = null);

public sealed record ProgrammeCalendarPreview(
    IReadOnlyList<ProgrammeScheduledCycle> Cycles, int? NextCycleIndex)
{
    public bool HasMoreDueCycles { get; init; }
}

public sealed class ProgrammeCalendar
{
    public const int MaximumPreviewCycles = 100;

    private readonly DateTime _anchorLocal;
    private readonly int _interval;
    private readonly ProgrammeIntervalUnit _unit;
    private readonly TimeZoneInfo _timeZone;

    public ProgrammeCalendar(DateTime anchorLocal, ProgrammeInterval recurrence, string timeZoneId)
    {
        var (unit, interval) = recurrence;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(interval);
        if (!Enum.IsDefined(unit))
        {
            throw new ArgumentOutOfRangeException(nameof(recurrence));
        }

        if (anchorLocal.Kind != DateTimeKind.Unspecified)
        {
            throw new ArgumentException("A programme anchor must be an unspecified local date and time.", nameof(anchorLocal));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(timeZoneId);
        _timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        if (!_timeZone.HasIanaId)
        {
            throw new ArgumentException("A programme requires an IANA timezone identifier.", nameof(timeZoneId));
        }

        _anchorLocal = anchorLocal;
        _interval = interval;
        _unit = unit;
    }

    // Always calculate from the persisted original anchor so clamping and DST do not accumulate drift.
    public DateTimeOffset? GetDueAt(int cycleIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cycleIndex);
        return AddInterval(_anchorLocal, cycleIndex);
    }

    public DateTimeOffset? GetDueAfterCompletion(DateTimeOffset acceptedCompletion)
    {
        var completionLocal = TimeZoneInfo.ConvertTime(acceptedCompletion, _timeZone).DateTime;
        return AddInterval(completionLocal, 1);
    }

    public ProgrammeCalendarPreview Preview(
        int firstCycleIndex, int count, ProgrammeCalendarWindow window)
    {
        var (through, endAt) = window;
        ArgumentOutOfRangeException.ThrowIfNegative(firstCycleIndex);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, MaximumPreviewCycles);
        var cycles = new List<ProgrammeScheduledCycle>(count);
        var nextIndex = firstCycleIndex;
        var dueAt = GetDueAt(nextIndex);
        while (cycles.Count < count && IsDue(dueAt, through, endAt))
        {
            cycles.Add(new ProgrammeScheduledCycle(nextIndex, dueAt!.Value));
            nextIndex++;
            dueAt = GetDueAt(nextIndex);
        }

        var exhausted = dueAt is null || (endAt.HasValue && dueAt > endAt);
        return new ProgrammeCalendarPreview(cycles.AsReadOnly(), exhausted ? null : nextIndex)
        {
            HasMoreDueCycles = IsDue(dueAt, through, endAt)
        };
    }

    private static bool IsDue(DateTimeOffset? dueAt, DateTimeOffset through, DateTimeOffset? endAt)
        => dueAt.HasValue && dueAt <= through && (!endAt.HasValue || dueAt <= endAt);

    private DateTimeOffset? AddInterval(DateTime anchor, int cycleIndex)
    {
        var units = (long)_interval * cycleIndex;
        try
        {
            var local = _unit switch
            {
                ProgrammeIntervalUnit.Day => anchor.AddDays(units),
                ProgrammeIntervalUnit.Week => anchor.AddDays(checked(units * 7)),
                ProgrammeIntervalUnit.Month => anchor.AddMonths(checked((int)units)),
                ProgrammeIntervalUnit.Year => anchor.AddYears(checked((int)units)),
                _ => throw new InvalidOperationException("Unsupported programme interval unit.")
            };
            return ResolveLocal(local);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private DateTimeOffset? ResolveLocal(DateTime local)
    {
        // Gap dates move to the first valid minute. Repeated hours use their earlier UTC occurrence.
        // Bound the search to cover a whole skipped date without an unbounded worker loop.
        for (var minute = 0; minute <= 24 * 60; minute++)
        {
            if (!_timeZone.IsInvalidTime(local))
            {
                var offset = _timeZone.IsAmbiguousTime(local)
                    ? _timeZone.GetAmbiguousTimeOffsets(local).Max()
                    : _timeZone.GetUtcOffset(local);
                return new DateTimeOffset(local, offset).ToUniversalTime();
            }

            local = local.AddTicks(-(local.Ticks % TimeSpan.TicksPerMinute)).AddMinutes(1);
        }

        throw new InvalidOperationException("The programme timezone has an unsupported local-time gap.");
    }
}

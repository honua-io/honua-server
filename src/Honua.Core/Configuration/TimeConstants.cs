// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Configuration;

/// <summary>
/// Centralized time constants used throughout the application for timeouts, TTL values, and intervals.
/// Prevents magic numbers and ensures consistency across caching, validation, and configuration.
/// </summary>
public static class TimeConstants
{
    #region Base Time Units (in seconds)

    /// <summary>
    /// 1 second in seconds (1).
    /// </summary>
    public const int OneSecond = 1;

    /// <summary>
    /// 1 minute in seconds (60).
    /// </summary>
    public const int OneMinute = 60;

    /// <summary>
    /// 1 hour in seconds (3,600).
    /// </summary>
    public const int OneHour = 60 * 60;

    /// <summary>
    /// 1 day in seconds (86,400).
    /// </summary>
    public const int OneDay = 24 * 60 * 60;

    #endregion

    #region Common Timeout Values (in seconds)

    /// <summary>
    /// 5 seconds - used for short operations like geometry validation.
    /// </summary>
    public const int FiveSeconds = 5;

    /// <summary>
    /// 10 seconds - used for tile generation timeouts.
    /// </summary>
    public const int TenSeconds = 10;

    /// <summary>
    /// 30 seconds - used for query timeouts and retry intervals.
    /// </summary>
    public const int ThirtySeconds = 30;

    /// <summary>
    /// 2 minutes in seconds (120) - used for request timeouts.
    /// </summary>
    public const int TwoMinutes = 2 * OneMinute;

    /// <summary>
    /// 5 minutes in seconds (300) - used for cache retry intervals and long operations.
    /// </summary>
    public const int FiveMinutes = 5 * OneMinute;

    /// <summary>
    /// 10 minutes in seconds (600) - used for maximum operation timeouts.
    /// </summary>
    public const int TenMinutes = 10 * OneMinute;

    /// <summary>
    /// 30 minutes in seconds (1,800) - used for default cache TTL.
    /// </summary>
    public const int ThirtyMinutes = 30 * OneMinute;

    #endregion

    #region Cache TTL Values (in seconds)

    /// <summary>
    /// 1 minute (60) - used for negative cache entries.
    /// </summary>
    public const int NegativeCacheTtl = OneMinute;

    /// <summary>
    /// 30 minutes (1,800) - used for layer metadata cache.
    /// </summary>
    public const int DefaultCacheTtl = ThirtyMinutes;

    /// <summary>
    /// 1 hour (3,600) - used for service metadata cache.
    /// </summary>
    public const int ServiceCacheTtl = OneHour;

    /// <summary>
    /// 24 hours (86,400) - maximum allowed cache TTL.
    /// </summary>
    public const int MaxCacheTtl = OneDay;

    #endregion

    #region TimeSpan Helpers

    /// <summary>
    /// Gets a TimeSpan representing 1 second.
    /// </summary>
    public static TimeSpan OneSecondTimeSpan => TimeSpan.FromSeconds(OneSecond);

    /// <summary>
    /// Gets a TimeSpan representing 5 seconds.
    /// </summary>
    public static TimeSpan FiveSecondsTimeSpan => TimeSpan.FromSeconds(FiveSeconds);

    /// <summary>
    /// Gets a TimeSpan representing 10 seconds.
    /// </summary>
    public static TimeSpan TenSecondsTimeSpan => TimeSpan.FromSeconds(TenSeconds);

    /// <summary>
    /// Gets a TimeSpan representing 30 seconds.
    /// </summary>
    public static TimeSpan ThirtySecondsTimeSpan => TimeSpan.FromSeconds(ThirtySeconds);

    /// <summary>
    /// Gets a TimeSpan representing 2 minutes.
    /// </summary>
    public static TimeSpan TwoMinutesTimeSpan => TimeSpan.FromSeconds(TwoMinutes);

    /// <summary>
    /// Gets a TimeSpan representing 5 minutes.
    /// </summary>
    public static TimeSpan FiveMinutesTimeSpan => TimeSpan.FromSeconds(FiveMinutes);

    /// <summary>
    /// Gets a TimeSpan representing 10 minutes.
    /// </summary>
    public static TimeSpan TenMinutesTimeSpan => TimeSpan.FromSeconds(TenMinutes);

    /// <summary>
    /// Gets a TimeSpan representing 1 minute.
    /// </summary>
    public static TimeSpan OneMinuteTimeSpan => TimeSpan.FromSeconds(OneMinute);

    /// <summary>
    /// Gets a TimeSpan representing 30 minutes.
    /// </summary>
    public static TimeSpan ThirtyMinutesTimeSpan => TimeSpan.FromSeconds(ThirtyMinutes);

    /// <summary>
    /// Gets a TimeSpan representing 1 hour.
    /// </summary>
    public static TimeSpan OneHourTimeSpan => TimeSpan.FromSeconds(OneHour);

    /// <summary>
    /// Gets a TimeSpan representing 1 day.
    /// </summary>
    public static TimeSpan OneDayTimeSpan => TimeSpan.FromSeconds(OneDay);

    #endregion

    #region Validation Helpers

    /// <summary>
    /// Validates that a timeout value is within reasonable bounds.
    /// </summary>
    /// <param name="timeoutSeconds">Timeout value in seconds</param>
    /// <param name="minSeconds">Minimum allowed timeout</param>
    /// <param name="maxSeconds">Maximum allowed timeout</param>
    /// <returns>True if timeout is within bounds, false otherwise</returns>
    public static bool IsValidTimeout(int timeoutSeconds, int minSeconds, int maxSeconds)
    {
        return timeoutSeconds >= minSeconds && timeoutSeconds <= maxSeconds;
    }

    /// <summary>
    /// Gets a human-readable description of a time duration in seconds.
    /// </summary>
    /// <param name="durationSeconds">Duration in seconds to format</param>
    /// <returns>Formatted string like "30 seconds" or "5 minutes"</returns>
    public static string FormatDuration(int durationSeconds)
    {
        return durationSeconds switch
        {
            < OneMinute => $"{durationSeconds} second{(durationSeconds == 1 ? "" : "s")}",
            < OneHour when durationSeconds % OneMinute == 0 => $"{durationSeconds / OneMinute} minute{(durationSeconds == OneMinute ? "" : "s")}",
            < OneDay when durationSeconds % OneHour == 0 => $"{durationSeconds / OneHour} hour{(durationSeconds == OneHour ? "" : "s")}",
            >= OneDay when durationSeconds % OneDay == 0 => $"{durationSeconds / OneDay} day{(durationSeconds == OneDay ? "" : "s")}",
            _ => $"{durationSeconds} seconds"
        };
    }

    #endregion
}

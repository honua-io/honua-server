namespace Honua.Core.Configuration;

/// <summary>
/// Standard time constants for consistent timeout and TTL values across the system.
/// </summary>
public static class TimeConstants
{
    /// <summary>
    /// Default cache TTL for short-lived data (5 minutes).
    /// </summary>
    public static readonly TimeSpan DefaultShortCacheTtl = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Default cache TTL for medium-lived data (30 minutes).
    /// </summary>
    public static readonly TimeSpan DefaultMediumCacheTtl = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Default cache TTL for long-lived data (2 hours).
    /// </summary>
    public static readonly TimeSpan DefaultLongCacheTtl = TimeSpan.FromHours(2);

    /// <summary>
    /// Maximum allowed cache TTL (24 hours).
    /// </summary>
    public static readonly TimeSpan MaxCacheTtl = TimeSpan.FromHours(24);

    /// <summary>
    /// Default HTTP timeout for external requests (30 seconds).
    /// </summary>
    public static readonly TimeSpan DefaultHttpTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Default database query timeout (60 seconds).
    /// </summary>
    public static readonly TimeSpan DefaultQueryTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// One second duration.
    /// </summary>
    public static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    /// <summary>
    /// One day duration.
    /// </summary>
    public static readonly TimeSpan OneDay = TimeSpan.FromDays(1);

    /// <summary>
    /// One hour duration.
    /// </summary>
    public static readonly TimeSpan OneHour = TimeSpan.FromHours(1);

    /// <summary>
    /// Five seconds duration.
    /// </summary>
    public static readonly TimeSpan FiveSeconds = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Ten seconds duration.
    /// </summary>
    public static readonly TimeSpan TenSeconds = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Five minutes duration.
    /// </summary>
    public static readonly TimeSpan FiveMinutes = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Two minutes duration.
    /// </summary>
    public static readonly TimeSpan TwoMinutes = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Thirty seconds duration.
    /// </summary>
    public const int ThirtySeconds = 30;

    /// <summary>
    /// One minute duration in seconds.
    /// </summary>
    public const int OneMinute = 60;

    /// <summary>
    /// Thirty minutes duration in seconds.
    /// </summary>
    public const int ThirtyMinutes = 30 * 60;

    /// <summary>
    /// Five seconds duration in seconds.
    /// </summary>
    public const int FiveSecondsInt = 5;

    /// <summary>
    /// Two minutes duration in seconds.
    /// </summary>
    public const int TwoMinutesInt = 2 * 60;
}

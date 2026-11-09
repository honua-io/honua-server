namespace Honua.Core.Configuration;

public static class TimeConstants
{
    public static readonly TimeSpan DefaultShortCacheTtl = TimeSpan.FromMinutes(5);

    public static readonly TimeSpan DefaultMediumCacheTtl = TimeSpan.FromMinutes(30);

    public static readonly TimeSpan DefaultLongCacheTtl = TimeSpan.FromHours(2);

    public static readonly TimeSpan MaxCacheTtl = TimeSpan.FromHours(24);

    public static readonly TimeSpan DefaultHttpTimeout = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan DefaultQueryTimeout = TimeSpan.FromSeconds(60);

    public static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    public static readonly TimeSpan OneDay = TimeSpan.FromDays(1);

    public static readonly TimeSpan OneHour = TimeSpan.FromHours(1);

    public static readonly TimeSpan FiveSeconds = TimeSpan.FromSeconds(5);

    public static readonly TimeSpan TenSeconds = TimeSpan.FromSeconds(10);

    public static readonly TimeSpan FiveMinutes = TimeSpan.FromMinutes(5);

    public static readonly TimeSpan TwoMinutes = TimeSpan.FromMinutes(2);

    public const int ThirtySeconds = 30;

    public const int OneMinute = 60;

    public const int ThirtyMinutes = 30 * 60;

    public const int FiveSecondsInt = 5;

    public const int TwoMinutesInt = 2 * 60;
}

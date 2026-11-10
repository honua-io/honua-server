using System.Globalization;

var winter = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
var summer = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
var honolulu = TimeZoneInfo.FindSystemTimeZoneById("Pacific/Honolulu");
var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
if (honolulu.GetUtcOffset(winter) != TimeSpan.FromHours(-10)
    || TimeZoneInfo.Local.Id != "Pacific/Honolulu"
    || newYork.GetUtcOffset(winter) != TimeSpan.FromHours(-5)
    || newYork.GetUtcOffset(summer) != TimeSpan.FromHours(-4)
    || 1234.5m.ToString("N1", CultureInfo.GetCultureInfo("de-DE")) != "1.234,5")
{
    throw new InvalidOperationException("Time-zone or ICU globalization proof failed.");
}
var temporary = Path.Combine(Path.GetTempPath(), "honua-runtime-probe");
File.WriteAllText(temporary, "writable");
File.Delete(temporary);
try
{
    File.WriteAllText("/app/honua-readonly-probe", "must fail");
    throw new InvalidOperationException("Application directory is writable.");
}
catch (Exception error) when (error is UnauthorizedAccessException or IOException)
{
    Console.WriteLine("PASS: Honolulu, New York DST, ICU de-DE, writable tmp, read-only app");
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections;
using System.Globalization;
using System.Linq;
using Honua.TestKit.Performance;
using NBomber.Contracts.Stats;
using NBomber.CSharp;

namespace Honua.LoadTests;

internal static class Program
{
    private static readonly string[] _knownScenarios = LoadTestScenarios.ScenarioNames.ToArray();

    private static readonly HashSet<string> _knownScenarioSet = new(_knownScenarios, StringComparer.OrdinalIgnoreCase);

    internal static IReadOnlyList<string> KnownScenarios => _knownScenarios;

    public static int Main(string[] args)
    {
        if (!LoadTestOptions.TryParse(args, out var options, out var error))
        {
            Console.Error.WriteLine(error);
            WriteHelp(Console.Error);
            return 1;
        }

        if (options.ShowHelp)
        {
            WriteHelp(Console.Out);
            return 0;
        }

        var targets = NormalizeTargets(options.TargetScenarios);
        if (targets.Count > 0)
        {
            var unknownTargets = targets.Where(target => !_knownScenarioSet.Contains(target)).ToArray();
            if (unknownTargets.Length > 0)
            {
                Console.Error.WriteLine($"Unknown scenario(s): {string.Join(", ", unknownTargets)}");
                WriteKnownScenarios(Console.Error);
                return 1;
            }
        }

        var profile = LoadTestProfile.FromName(options.Profile);
        if (options.PrintProfile)
        {
            // The capacity-soak producer reads this to assert the profile it is about to run
            // against the frozen lock BEFORE a 60-minute run starts, so a drifted profile fails
            // in seconds instead of producing a receipt that claims the wrong concurrency.
            Console.WriteLine(
                "{\"profile\":\"" + options.Profile + "\",\"totalVirtualUsers\":" +
                profile.TotalVirtualUsers.ToString(CultureInfo.InvariantCulture) +
                ",\"rampUpSeconds\":" + profile.RampUp.TotalSeconds.ToString("R", CultureInfo.InvariantCulture) +
                ",\"steadyStateSeconds\":" + profile.Duration.TotalSeconds.ToString("R", CultureInfo.InvariantCulture) +
                ",\"rampDownSeconds\":" + profile.RampDown.TotalSeconds.ToString("R", CultureInfo.InvariantCulture) + "}");
            return 0;
        }

        if (options.Duration is { } duration)
        {
            profile = profile.WithDuration(duration);
        }

        if (options.RampUp is { } rampUp)
        {
            profile = profile.WithRampUp(rampUp);
        }

        if (options.RampDown is { } rampDown)
        {
            profile = profile.WithRampDown(rampDown);
        }

        var baseUrl = NormalizeBaseUrl(options.BaseUrl);
        var reportFolder = string.IsNullOrWhiteSpace(options.ReportFolder)
            ? "load-test-reports"
            : options.ReportFolder;

        Console.WriteLine($"Running load tests against {baseUrl} (profile: {options.Profile})");
        if (targets.Count > 0)
        {
            Console.WriteLine($"Target scenarios: {string.Join(", ", targets)}");
        }

        Console.WriteLine($"Reports: {reportFolder}");

        // An interrupted repeat must not leave a previous run's successful receipt input.
        if (!string.IsNullOrWhiteSpace(options.StatsOut))
        {
            File.Delete(options.StatsOut);
        }

        var context = LoadTestScenarios.CreateLoadTestSuite(
            baseUrl,
            profile,
            options.LayerId,
            options.CollectionId,
            options.TileMatrixSet,
            reportFolder,
            options.ReportFormats);

        if (targets.Count > 0)
        {
            context = context.WithTargetScenarios(targets.ToArray());
        }

        if (options.Profile.Equals("soak", StringComparison.OrdinalIgnoreCase))
        {
            // A soak measures the entire window, including a failing candidate. NBomber's
            // default 5,000-error circuit breaker otherwise stops it during ramp-up.
            // This controls early termination only: every failure is still counted and
            // EvaluateResults applies the unchanged failure-rate acceptance threshold.
            var settings = context.RegisteredScenarios.Select(scenario =>
                $"{{\"ScenarioName\":{JsonString(scenario.ScenarioName)},\"MaxFailCount\":{int.MaxValue}}}");
            context = context.LoadConfig("{\"GlobalSettings\":{\"ScenariosSettings\":[" +
                string.Join(",", settings) + "]}}");
        }

        context = context
            .DisplayConsoleMetrics(!Console.IsOutputRedirected)
            .WithReportingSinks(new LoadProgressSink())
            .WithScenarioCompletionTimeout(LoadTestScenarios.RequestTimeout + TimeSpan.FromSeconds(10));

        var budget = profile.RampUp + profile.Duration + profile.RampDown
            + LoadTestScenarios.RequestTimeout + TimeSpan.FromMinutes(2);
        Console.WriteLine($"Load harness completion budget: {budget} (including request drain and final statistics).");
        if (!LoadRunDeadline.TryComplete(context.Run, budget, out var stats))
        {
            Console.Error.WriteLine($"Load harness did not complete within {budget}; refusing incomplete statistics. Last progress: {LoadProgressSink.LastProgress}");
            return 124;
        }

        var expectedDuration = profile.RampUp + profile.Duration + profile.RampDown;
        var expectedNames = targets.Count > 0 ? targets : _knownScenarioSet;
        if (!expectedNames.SetEquals(stats.ScenarioStats.Select(scenario => scenario.ScenarioName))
            || stats.ScenarioStats.Any(scenario => scenario.Duration < expectedDuration
                || scenario.Ok.Request.Count + scenario.Fail.Request.Count == 0))
        {
            Console.Error.WriteLine($"Load harness returned an incomplete run; every selected scenario must execute {expectedDuration} and report requests. Refusing partial statistics.");
            return 1;
        }

        if (!string.IsNullOrWhiteSpace(options.StatsOut))
        {
            WriteStatsSummary(stats, options.StatsOut!, options.Profile, baseUrl);
            Console.WriteLine($"Machine-readable stats: {options.StatsOut}");
        }

        return EvaluateResults(stats, options.MaxFailureRate);
    }

    private static string NormalizeBaseUrl(string baseUrl)
    {
        return baseUrl.TrimEnd('/');
    }

    private static HashSet<string> NormalizeTargets(string[] targets)
    {
        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets.Where(t => !string.IsNullOrWhiteSpace(t)))
        {
            normalized.Add(target.Trim());
        }

        return normalized;
    }

    private static int EvaluateResults(object stats, double? maxFailureRate)
    {
        if (stats is null)
        {
            Console.Error.WriteLine("NBomber returned no stats.");
            return 1;
        }

        var totalRequests = ReadInt(stats, "AllRequestCount");
        var failedRequests = ReadInt(stats, "AllFailCount");
        var failedThresholds = CountThresholdFailures(stats);
        var failureRate = totalRequests > 0
            ? (double)failedRequests / totalRequests
            : 0d;

        Console.WriteLine($"Completed load test. Total requests: {totalRequests}, failed: {failedRequests}.");
        Console.WriteLine($"Failure rate: {failureRate:P4}.");

        if (failedThresholds > 0)
        {
            Console.Error.WriteLine(
                $"Load test failures detected. Failed requests: {failedRequests}. Failed thresholds: {failedThresholds}.");
            return 1;
        }

        if (failedRequests > 0)
        {
            if (!maxFailureRate.HasValue || maxFailureRate <= 0d)
            {
                Console.Error.WriteLine(
                    $"Load test failures detected. Failed requests: {failedRequests}. Failed thresholds: {failedThresholds}.");
                return 1;
            }

            if (failureRate > maxFailureRate.Value)
            {
                Console.Error.WriteLine(
                    $"Load test failures detected. Failure rate {failureRate:P4} exceeds allowed {maxFailureRate.Value:P4}.");
                return 1;
            }

            Console.WriteLine(
                $"Failed requests within allowed failure rate ({maxFailureRate.Value:P4}).");
        }

        return 0;
    }

    private static int CountThresholdFailures(object stats)
    {
        var thresholds = ReadEnumerable(stats, "Thresholds");
        if (thresholds is null)
        {
            return 0;
        }

        var failures = 0;
        foreach (var threshold in thresholds)
        {
            if (threshold is null)
            {
                continue;
            }

            var hasFailure = ReadBool(threshold, "IsFailed")
                ?? ReadBool(threshold, "HasErrors")
                ?? ReadBool(threshold, "IsError")
                ?? false;

            if (!hasFailure)
            {
                var errorCount = ReadInt(threshold, "ErrorCount");
                hasFailure = errorCount > 0;
            }

            if (hasFailure)
            {
                failures++;
            }
        }

        return failures;
    }

    private static IEnumerable? ReadEnumerable(object target, string propertyName)
    {
        var value = ReadProperty(target, propertyName);
        return value as IEnumerable;
    }

    /// <summary>
    /// Writes the run's aggregate and per-scenario statistics as JSON.
    /// </summary>
    /// <remarks>
    /// The HTML/CSV reports NBomber writes are for humans. The capacity-soak receipt producer
    /// (<c>scripts/soak/</c>) needs the same numbers as data, and it must never silently substitute
    /// a default for a figure it could not read: any statistic missing from the stats object is
    /// emitted as JSON <c>null</c> so the consumer fails closed on it rather than publishing a
    /// receipt built on a zero that never happened.
    /// </remarks>
    private static void WriteStatsSummary(object stats, string path, string profile, string baseUrl)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append("{\n");
        builder.Append(CultureInfo.InvariantCulture, $"  \"generatedAt\": \"{DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)}\",\n");
        builder.Append(CultureInfo.InvariantCulture, $"  \"profile\": {JsonString(profile)},\n");
        builder.Append(CultureInfo.InvariantCulture, $"  \"baseUrl\": {JsonString(baseUrl)},\n");
        builder.Append(CultureInfo.InvariantCulture, $"  \"durationSeconds\": {JsonNumber(ReadDurationSeconds(stats))},\n");
        builder.Append(CultureInfo.InvariantCulture, $"  \"allRequestCount\": {JsonNumber(ReadNullableDouble(stats, "AllRequestCount"))},\n");
        builder.Append(CultureInfo.InvariantCulture, $"  \"allOkCount\": {JsonNumber(ReadNullableDouble(stats, "AllOkCount"))},\n");
        builder.Append(CultureInfo.InvariantCulture, $"  \"allFailCount\": {JsonNumber(ReadNullableDouble(stats, "AllFailCount"))},\n");
        builder.Append("  \"scenarios\": [\n");

        var scenarios = ReadEnumerable(stats, "ScenarioStats");
        var first = true;
        if (scenarios is not null)
        {
            foreach (var scenario in scenarios)
            {
                if (scenario is null)
                {
                    continue;
                }

                if (!first)
                {
                    builder.Append(",\n");
                }

                first = false;
                var ok = ReadProperty(scenario, "Ok");
                var fail = ReadProperty(scenario, "Fail");
                var latency = ok is null ? null : ReadProperty(ok, "Latency");
                builder.Append("    {\n");
                builder.Append(CultureInfo.InvariantCulture, $"      \"name\": {JsonString(ReadProperty(scenario, "ScenarioName") as string ?? string.Empty)},\n");
                builder.Append(CultureInfo.InvariantCulture, $"      \"durationSeconds\": {JsonNumber(ReadDurationSeconds(scenario))},\n");
                builder.Append(CultureInfo.InvariantCulture, $"      \"okCount\": {JsonNumber(ReadRequestStat(ok, "Count"))},\n");
                builder.Append(CultureInfo.InvariantCulture, $"      \"failCount\": {JsonNumber(ReadRequestStat(fail, "Count"))},\n");
                builder.Append(CultureInfo.InvariantCulture, $"      \"okRps\": {JsonNumber(ReadRequestStat(ok, "RPS"))},\n");
                builder.Append(CultureInfo.InvariantCulture, $"      \"meanMs\": {JsonNumber(ReadNullableDouble(latency, "MeanMs"))},\n");
                builder.Append(CultureInfo.InvariantCulture, $"      \"maxMs\": {JsonNumber(ReadNullableDouble(latency, "MaxMs"))},\n");
                builder.Append(CultureInfo.InvariantCulture, $"      \"p95Ms\": {JsonNumber(ReadNullableDouble(latency, "Percent95"))},\n");
                builder.Append(CultureInfo.InvariantCulture, $"      \"p99Ms\": {JsonNumber(ReadNullableDouble(latency, "Percent99"))}\n");
                builder.Append("    }");
            }
        }

        builder.Append(first ? "  ]\n" : "\n  ]\n");
        builder.Append("}\n");

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, builder.ToString());
    }

    private static double? ReadRequestStat(object? measurement, string propertyName)
    {
        if (measurement is null)
        {
            return null;
        }

        var request = ReadProperty(measurement, "Request");
        return request is null ? null : ReadNullableDouble(request, propertyName);
    }

    private static double? ReadDurationSeconds(object target)
    {
        var value = ReadProperty(target, "Duration");
        return value is TimeSpan duration ? duration.TotalSeconds : null;
    }

    private static double? ReadNullableDouble(object? target, string propertyName)
    {
        if (target is null)
        {
            return null;
        }

        var value = ReadProperty(target, propertyName);
        return value switch
        {
            null => null,
            double doubleValue => doubleValue,
            float floatValue => floatValue,
            int intValue => intValue,
            long longValue => longValue,
            decimal decimalValue => (double)decimalValue,
            IConvertible convertible => ConvertToDouble(convertible),
            _ => null
        };
    }

    private static double? ConvertToDouble(IConvertible convertible)
    {
        try
        {
            return convertible.ToDouble(CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }
        catch (InvalidCastException)
        {
            return null;
        }
    }

    private static string JsonNumber(double? value) =>
        value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value)
            ? "null"
            : value.Value.ToString("R", CultureInfo.InvariantCulture);

    private static string JsonString(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static int ReadInt(object target, string propertyName)
    {
        var value = ReadProperty(target, propertyName);
        if (value is null)
        {
            return 0;
        }

        if (value is int intValue)
        {
            return intValue;
        }

        if (value is long longValue)
        {
            return longValue > int.MaxValue ? int.MaxValue : (int)longValue;
        }

        if (value is IConvertible convertible)
        {
            try
            {
                return convertible.ToInt32(CultureInfo.InvariantCulture);
            }
            catch (FormatException)
            {
                return 0;
            }
            catch (OverflowException)
            {
                return 0;
            }
        }

        return 0;
    }

    private static bool? ReadBool(object target, string propertyName)
    {
        var value = ReadProperty(target, propertyName);
        return value as bool?;
    }

    private static object? ReadProperty(object target, string propertyName)
    {
        var property = target.GetType().GetProperty(propertyName);
        return property?.GetValue(target);
    }

    private static void WriteHelp(TextWriter writer)
    {
        writer.WriteLine("Honua load/soak test runner");
        writer.WriteLine("");
        writer.WriteLine("Options:");
        writer.WriteLine("  --base-url <url>         Base URL for Honua Server (default: http://localhost:5000)");
        writer.WriteLine("  --profile <name>         Load profile: quick, nightly, soak (default: quick)");
        writer.WriteLine("  --duration <timespan>    Override steady-state duration (e.g., 30m, 00:30:00)");
        writer.WriteLine("  --ramp-up <timespan>     Override ramp-up duration");
        writer.WriteLine("  --ramp-down <timespan>   Override ramp-down duration");
        writer.WriteLine("  --layer-id <id>          Feature layer id (default: 0)");
        writer.WriteLine("  --collection-id <id>     OGC collection id (default: 0)");
        writer.WriteLine("  --tile-matrix-set <id>   Tile matrix set id (default: WebMercatorQuad)");
        writer.WriteLine("  --target-scenarios <csv> Comma-separated scenario names to run");
        writer.WriteLine("  --report-folder <path>   Output directory for NBomber reports");
        writer.WriteLine("  --max-failure-rate <n>   Max failed request ratio (0-1, e.g. 0.0001 = 0.01%)");
        writer.WriteLine("  --stats-out <path>       Write aggregate/per-scenario statistics as JSON");
        writer.WriteLine("  --print-profile          Print the resolved profile as JSON and exit");
        writer.WriteLine("  --report-formats <csv>   NBomber report formats (html,csv,md,txt or none; default html,csv)");
        writer.WriteLine("  --help                   Show this help");
        writer.WriteLine("");
        WriteKnownScenarios(writer);
    }

    private static void WriteKnownScenarios(TextWriter writer)
    {
        writer.WriteLine("Known scenarios:");
        foreach (var scenario in _knownScenarios)
        {
            writer.WriteLine($"  - {scenario}");
        }
    }
}

internal sealed class LoadTestOptions
{
    public string BaseUrl { get; private set; } = "http://localhost:5000";
    public string Profile { get; private set; } = "quick";
    public TimeSpan? Duration { get; private set; }
    public TimeSpan? RampUp { get; private set; }
    public TimeSpan? RampDown { get; private set; }
    public string LayerId { get; private set; } = "0";
    public string CollectionId { get; private set; } = "0";
    public string TileMatrixSet { get; private set; } = "WebMercatorQuad";
    public string[] TargetScenarios { get; private set; } = Array.Empty<string>();
    public string ReportFolder { get; private set; } = "load-test-reports";
    public double? MaxFailureRate { get; private set; }
    public string? StatsOut { get; private set; }

    /// <summary>
    /// Report formats NBomber renders at the end of the run. HTML and CSV are the default because
    /// they are what a human reads after a nightly. An hour-long soak produces millions of data
    /// points, and rendering them all is neither free nor needed by an automated consumer: the
    /// capacity-soak producer reads <c>--stats-out</c> instead and asks for a cheaper set here.
    /// </summary>
    public ReportFormat[] ReportFormats { get; private set; } = new[] { ReportFormat.Html, ReportFormat.Csv };
    public bool PrintProfile { get; private set; }
    public bool ShowHelp { get; private set; }

    public static bool TryParse(string[] args, out LoadTestOptions options, out string error)
    {
        options = new LoadTestOptions();
        error = string.Empty;

        var targets = new List<string>();

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            switch (arg)
            {
                case "--base-url":
                    if (!TryReadValue(args, ref index, out var baseUrl, out error))
                    {
                        return false;
                    }

                    options.BaseUrl = baseUrl;
                    break;
                case "--profile":
                    if (!TryReadValue(args, ref index, out var profile, out error))
                    {
                        return false;
                    }

                    options.Profile = profile;
                    break;
                case "--duration":
                    if (!TryReadTimeSpan(args, ref index, out var duration, out error))
                    {
                        return false;
                    }

                    options.Duration = duration;
                    break;
                case "--ramp-up":
                    if (!TryReadTimeSpan(args, ref index, out var rampUp, out error))
                    {
                        return false;
                    }

                    options.RampUp = rampUp;
                    break;
                case "--ramp-down":
                    if (!TryReadTimeSpan(args, ref index, out var rampDown, out error))
                    {
                        return false;
                    }

                    options.RampDown = rampDown;
                    break;
                case "--layer-id":
                    if (!TryReadValue(args, ref index, out var layerId, out error))
                    {
                        return false;
                    }

                    options.LayerId = layerId;
                    break;
                case "--collection-id":
                    if (!TryReadValue(args, ref index, out var collectionId, out error))
                    {
                        return false;
                    }

                    options.CollectionId = collectionId;
                    break;
                case "--tile-matrix-set":
                    if (!TryReadValue(args, ref index, out var tileMatrixSetId, out error))
                    {
                        return false;
                    }

                    options.TileMatrixSet = tileMatrixSetId;
                    break;
                case "--target-scenarios":
                    if (!TryReadValue(args, ref index, out var targetScenarioList, out error))
                    {
                        return false;
                    }

                    foreach (var target in targetScenarioList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        targets.Add(target);
                    }

                    break;
                case "--report-folder":
                    if (!TryReadValue(args, ref index, out var reportFolder, out error))
                    {
                        return false;
                    }

                    options.ReportFolder = reportFolder;
                    break;
                case "--report-formats":
                    if (!TryReadValue(args, ref index, out var formats, out error))
                    {
                        return false;
                    }

                    if (!TryParseReportFormats(formats, out var parsedFormats, out error))
                    {
                        return false;
                    }

                    options.ReportFormats = parsedFormats;
                    break;
                case "--print-profile":
                    options.PrintProfile = true;
                    break;
                case "--stats-out":
                    if (!TryReadValue(args, ref index, out var statsOut, out error))
                    {
                        return false;
                    }

                    options.StatsOut = statsOut;
                    break;
                case "--max-failure-rate":
                    if (!TryReadDouble(args, ref index, out var maxFailureRate, out error))
                    {
                        return false;
                    }

                    if (maxFailureRate < 0d || maxFailureRate > 1d)
                    {
                        error = "Max failure rate must be between 0 and 1.";
                        return false;
                    }

                    options.MaxFailureRate = maxFailureRate;
                    break;
                case "--help":
                case "-h":
                    options.ShowHelp = true;
                    break;
                default:
                    error = $"Unknown option: {arg}";
                    return false;
            }
        }

        options.TargetScenarios = targets.ToArray();

        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            error = "Base URL must be provided.";
            return false;
        }

        return true;
    }

    private static bool TryReadValue(string[] args, ref int index, out string value, out string error)
    {
        error = string.Empty;
        value = string.Empty;

        if (index + 1 >= args.Length)
        {
            error = $"Missing value for {args[index]}";
            return false;
        }

        index++;
        value = args[index];
        return true;
    }

    private static bool TryReadTimeSpan(string[] args, ref int index, out TimeSpan value, out string error)
    {
        if (!TryReadValue(args, ref index, out var rawValue, out error))
        {
            value = default;
            return false;
        }

        if (!TryParseDuration(rawValue, out value))
        {
            error = $"Invalid duration: {rawValue}";
            return false;
        }

        return true;
    }

    private static bool TryParseReportFormats(string value, out ReportFormat[] formats, out string error)
    {
        error = string.Empty;
        var parsed = new List<ReportFormat>();
        foreach (var token in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(token, "none", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!Enum.TryParse<ReportFormat>(token, ignoreCase: true, out var format))
            {
                formats = Array.Empty<ReportFormat>();
                error = $"Unknown report format: {token}";
                return false;
            }

            parsed.Add(format);
        }

        formats = parsed.ToArray();
        return true;
    }

    private static bool TryReadDouble(string[] args, ref int index, out double value, out string error)
    {
        if (!TryReadValue(args, ref index, out var rawValue, out error))
        {
            value = 0d;
            return false;
        }

        if (!double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            error = $"Invalid number: {rawValue}";
            return false;
        }

        return true;
    }

    private static bool TryParseDuration(string value, out TimeSpan duration)
    {
        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out duration))
        {
            return true;
        }

        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            duration = default;
            return false;
        }

        if (trimmed.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseWithUnit(trimmed, 2, TimeSpan.FromMilliseconds, out duration);
        }

        var unit = trimmed[^1];
        var numberPart = trimmed[..^1];
        return unit switch
        {
            's' or 'S' => TryParseWithUnit(numberPart, TimeSpan.FromSeconds, out duration),
            'm' or 'M' => TryParseWithUnit(numberPart, TimeSpan.FromMinutes, out duration),
            'h' or 'H' => TryParseWithUnit(numberPart, TimeSpan.FromHours, out duration),
            'd' or 'D' => TryParseWithUnit(numberPart, TimeSpan.FromDays, out duration),
            _ => FailDuration(out duration)
        };
    }

    private static bool TryParseWithUnit(string value, Func<double, TimeSpan> factory, out TimeSpan duration)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            duration = factory(number);
            return true;
        }

        duration = default;
        return false;
    }

    private static bool TryParseWithUnit(string value, int trimLength, Func<double, TimeSpan> factory, out TimeSpan duration)
    {
        if (value.Length <= trimLength)
        {
            duration = default;
            return false;
        }

        return TryParseWithUnit(value[..^trimLength], factory, out duration);
    }

    private static bool FailDuration(out TimeSpan duration)
    {
        duration = default;
        return false;
    }
}

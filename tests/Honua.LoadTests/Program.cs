// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections;
using System.Globalization;
using Honua.TestKit.Performance;
using NBomber.CSharp;
using NBomber.Contracts.Stats;

namespace Honua.LoadTests;

internal static class Program
{
    private static readonly string[] _knownScenarios =
    {
        "feature_query_load",
        "spatial_query_load",
        "ogc_query_load",
        "cql_filter_load",
        "odata_query_load",
        "connection_pool_stress",
        "memory_stress",
        "tiles_load"
    };

    private static readonly HashSet<string> _knownScenarioSet = new(_knownScenarios, StringComparer.OrdinalIgnoreCase);

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

        var context = LoadTestScenarios.CreateLoadTestSuite(
            baseUrl,
            profile,
            options.LayerId,
            options.CollectionId,
            options.TileMatrixSet,
            reportFolder,
            new[] { ReportFormat.Html, ReportFormat.Csv });

        if (targets.Count > 0)
        {
            context = context.WithTargetScenarios(targets.ToArray());
        }

        var stats = context.Run();
        return EvaluateResults(stats);
    }

    private static string NormalizeBaseUrl(string baseUrl)
    {
        return baseUrl.TrimEnd('/');
    }

    private static HashSet<string> NormalizeTargets(string[] targets)
    {
        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets)
        {
            if (!string.IsNullOrWhiteSpace(target))
            {
                normalized.Add(target.Trim());
            }
        }

        return normalized;
    }

    private static int EvaluateResults(object stats)
    {
        if (stats is null)
        {
            Console.Error.WriteLine("NBomber returned no stats.");
            return 1;
        }

        var totalRequests = ReadInt(stats, "AllRequestCount");
        var failedRequests = ReadInt(stats, "AllFailCount");
        var failedThresholds = CountThresholdFailures(stats);

        Console.WriteLine($"Completed load test. Total requests: {totalRequests}, failed: {failedRequests}.");

        if (failedRequests > 0 || failedThresholds > 0)
        {
            Console.Error.WriteLine(
                $"Load test failures detected. Failed requests: {failedRequests}. Failed thresholds: {failedThresholds}.");
            return 1;
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

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.TestKit.Performance;
using NBomber.CSharp;

namespace Honua.LoadTests;

internal static class Program
{
    private static int Main(string[] args)
    {
        LoadTestOptions options;
        try
        {
            options = LoadTestOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            PrintUsage();
            return 1;
        }

        if (options.ShowHelp)
        {
            PrintUsage();
            return 0;
        }

        var profile = LoadTestProfile.FromName(options.Profile);
        if (options.Duration is not null)
        {
            profile = profile.WithDuration(options.Duration.Value);
        }

        if (options.RampUp is not null)
        {
            profile = profile.WithRampUp(options.RampUp.Value);
        }

        if (options.RampDown is not null)
        {
            profile = profile.WithRampDown(options.RampDown.Value);
        }

        var context = LoadTestScenarios.CreateLoadTestSuite(
            options.BaseUrl,
            profile,
            options.LayerId,
            options.CollectionId,
            options.TileMatrixSetId,
            options.ReportFolder);

        context = NBomberRunner.WithTestSuite(context, "honua-load-tests");
        context = NBomberRunner.WithTestName(context, $"{options.Profile}-load");

        if (options.TargetScenarios.Length > 0)
        {
            context = NBomberRunner.WithTargetScenarios(context, options.TargetScenarios);
        }

        NBomberRunner.Run(context);
        return 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Honua load/soak test runner");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run --project tests/Honua.LoadTests -- [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --base-url <url>            Base URL for Honua Server (default: http://localhost:5000)");
        Console.WriteLine("  --profile <quick|nightly|soak>");
        Console.WriteLine("                              Load profile (default: quick)");
        Console.WriteLine("  --duration <timespan>       Override steady-state duration (e.g., 30m, 00:30:00)");
        Console.WriteLine("  --ramp-up <timespan>        Override ramp-up duration");
        Console.WriteLine("  --ramp-down <timespan>      Override ramp-down duration");
        Console.WriteLine("  --layer-id <id>             Feature layer id (default: 0)");
        Console.WriteLine("  --collection-id <id>        OGC collection id (default: 0)");
        Console.WriteLine("  --tile-matrix-set <id>      Tile matrix set id (default: WebMercatorQuad)");
        Console.WriteLine("  --report-folder <path>      Output folder for NBomber reports");
        Console.WriteLine("  --target-scenarios <list>   Comma-separated scenario names to run");
        Console.WriteLine("  --help, -h                  Show this help");
        Console.WriteLine();
        Console.WriteLine("Environment variables:");
        Console.WriteLine("  HONUA_LOAD_BASE_URL, BASE_URL");
        Console.WriteLine("  HONUA_LOAD_PROFILE");
        Console.WriteLine("  HONUA_LOAD_DURATION");
        Console.WriteLine("  HONUA_LOAD_RAMP_UP");
        Console.WriteLine("  HONUA_LOAD_RAMP_DOWN");
        Console.WriteLine("  HONUA_LOAD_LAYER_ID");
        Console.WriteLine("  HONUA_LOAD_COLLECTION_ID");
        Console.WriteLine("  HONUA_LOAD_TILE_MATRIX_SET_ID");
        Console.WriteLine("  HONUA_LOAD_REPORT_FOLDER");
        Console.WriteLine("  HONUA_LOAD_TARGET_SCENARIOS");
    }

    private sealed class LoadTestOptions
    {
        public required string BaseUrl { get; init; }
        public required string Profile { get; init; }
        public TimeSpan? Duration { get; init; }
        public TimeSpan? RampUp { get; init; }
        public TimeSpan? RampDown { get; init; }
        public required string LayerId { get; init; }
        public required string CollectionId { get; init; }
        public required string TileMatrixSetId { get; init; }
        public required string ReportFolder { get; init; }
        public string[] TargetScenarios { get; init; } = Array.Empty<string>();
        public bool ShowHelp { get; init; }

        public static LoadTestOptions Parse(string[] args)
        {
            var baseUrl = GetEnvOrDefault(_baseUrlEnvNames, "http://localhost:5000");
            var profile = GetEnvOrDefault("HONUA_LOAD_PROFILE", "quick");
            var duration = GetEnvDuration("HONUA_LOAD_DURATION");
            var rampUp = GetEnvDuration("HONUA_LOAD_RAMP_UP");
            var rampDown = GetEnvDuration("HONUA_LOAD_RAMP_DOWN");
            var layerId = GetEnvOrDefault("HONUA_LOAD_LAYER_ID", "0");
            var collectionId = GetEnvOrDefault("HONUA_LOAD_COLLECTION_ID", "0");
            var tileMatrixSetId = GetEnvOrDefault("HONUA_LOAD_TILE_MATRIX_SET_ID", "WebMercatorQuad");
            var reportFolder = GetEnvOrDefault("HONUA_LOAD_REPORT_FOLDER", "load-test-reports");
            var targetScenarios = ParseList(Environment.GetEnvironmentVariable("HONUA_LOAD_TARGET_SCENARIOS"));
            var showHelp = false;

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                switch (arg)
                {
                    case "--base-url":
                        baseUrl = NextValue(args, ref i, arg);
                        break;
                    case "--profile":
                        profile = NextValue(args, ref i, arg);
                        break;
                    case "--duration":
                        duration = ParseDuration(NextValue(args, ref i, arg));
                        break;
                    case "--ramp-up":
                        rampUp = ParseDuration(NextValue(args, ref i, arg));
                        break;
                    case "--ramp-down":
                        rampDown = ParseDuration(NextValue(args, ref i, arg));
                        break;
                    case "--layer-id":
                        layerId = NextValue(args, ref i, arg);
                        break;
                    case "--collection-id":
                        collectionId = NextValue(args, ref i, arg);
                        break;
                    case "--tile-matrix-set":
                        tileMatrixSetId = NextValue(args, ref i, arg);
                        break;
                    case "--report-folder":
                        reportFolder = NextValue(args, ref i, arg);
                        break;
                    case "--target-scenarios":
                        targetScenarios = ParseList(NextValue(args, ref i, arg));
                        break;
                    case "--help":
                    case "-h":
                        showHelp = true;
                        break;
                    default:
                        throw new ArgumentException($"Unknown option: {arg}");
                }
            }

            return new LoadTestOptions
            {
                BaseUrl = baseUrl,
                Profile = profile,
                Duration = duration,
                RampUp = rampUp,
                RampDown = rampDown,
                LayerId = layerId,
                CollectionId = collectionId,
                TileMatrixSetId = tileMatrixSetId,
                ReportFolder = reportFolder,
                TargetScenarios = targetScenarios,
                ShowHelp = showHelp
            };
        }

        private static string GetEnvOrDefault(string name, string fallback)
        {
            var value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        private static string GetEnvOrDefault(string[] names, string fallback)
        {
            foreach (var name in names)
            {
                var value = Environment.GetEnvironmentVariable(name);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return fallback;
        }

        private static readonly string[] _baseUrlEnvNames = { "HONUA_LOAD_BASE_URL", "BASE_URL" };

        private static TimeSpan? GetEnvDuration(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return ParseDuration(value);
        }

        private static TimeSpan ParseDuration(string value)
        {
            if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }

            if (value.Length > 1)
            {
                var unit = char.ToLowerInvariant(value[^1]);
                if (double.TryParse(value[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
                {
                    return unit switch
                    {
                        'h' => TimeSpan.FromHours(amount),
                        'm' => TimeSpan.FromMinutes(amount),
                        's' => TimeSpan.FromSeconds(amount),
                        _ => throw new ArgumentException($"Unsupported duration format: {value}")
                    };
                }
            }

            throw new ArgumentException($"Unsupported duration format: {value}");
        }

        private static string NextValue(string[] args, ref int index, string optionName)
        {
            if (index + 1 >= args.Length)
            {
                throw new ArgumentException($"Missing value for {optionName}");
            }

            index++;
            return args[index];
        }

        private static string[] ParseList(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return Array.Empty<string>();
            }

            return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
    }
}

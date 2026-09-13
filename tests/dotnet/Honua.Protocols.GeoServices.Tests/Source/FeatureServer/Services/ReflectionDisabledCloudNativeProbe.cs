// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

/// <summary>
/// Re-runs the cloud-native writers in a child process with JSON reflection disabled.
/// </summary>
/// <remarks>
/// <para>
/// <c>Honua.Server.csproj</c> sets <c>JsonSerializerIsReflectionEnabledByDefault=false</c>, so every
/// shipped image (AOT and JIT) has no reflection fallback. The xUnit host runs with reflection
/// enabled, and the switch is read once per process, so an in-process test cannot see a
/// reflection-based serializer call: the fallback simply succeeds. honua-server#4747 shipped
/// exactly that — <c>f=parquet</c> returned a 500 envelope on the candidate image while every
/// unit test stayed green.
/// </para>
/// <para>
/// This type is the test assembly's entry point. <see cref="RunAsync"/> starts
/// <c>dotnet exec</c> on the test assembly with a copy of its runtimeconfig that turns the switch
/// off. The child writes the payload to disk and reports the switch state plus a canary that
/// proves the reflection overload really throws in that process.
/// </para>
/// </remarks>
internal static class ReflectionDisabledCloudNativeProbe
{
    public const string GeoParquetFormat = "geoparquet";
    public const string GeoArrowFormat = "geoarrow";
    public const string ReflectionStatePrefix = "reflection-enabled=";
    public const string CanaryPrefix = "reflection-canary=";

    private const string ReflectionSwitch = "System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault";

    // The exact shape the writer used to pass to the reflection-based overload.
    private static readonly string[] CanaryValue = ["Point"];

    /// <summary>
    /// Independently specified fixture rows. The Z row comes first so the sorted
    /// <c>geometry_types</c> order is not an accident of row order.
    /// </summary>
    public static readonly ImmutableArray<ProbeRow> Rows =
    [
        new(1, "Honolulu Harbor", 350000, -157.8583, 21.3069, 12.5),
        new(2, "Hilo Bay", 45000, -155.0868, 19.7297, null),
        new(3, "Kahului", 26000, -156.4729, 20.8893, null),
        new(4, "Līhuʻe", 6500, -159.3711, 21.9811, null),
        new(5, "Kailua-Kona", 19000, -155.9969, 19.64, null),
        new(6, "Lāhainā \"Front St\"", null, -156.6825, 20.8783, null),
    ];

    public static async Task<int> Main(string[] args)
    {
        if (args is not [var format, var outputPath])
        {
            await Console.Error.WriteLineAsync("usage: <geoparquet|geoarrow> <output-path>");
            return 2;
        }

        Console.WriteLine($"{ReflectionStatePrefix}{JsonSerializer.IsReflectionEnabledByDefault}");
        try
        {
            _ = JsonSerializer.Serialize(CanaryValue);
            Console.WriteLine($"{CanaryPrefix}serialized");
        }
        catch (InvalidOperationException)
        {
            Console.WriteLine($"{CanaryPrefix}threw");
        }

        var result = QueryResult<Feature>.Create(Rows.Length, [.. Rows.Select(CreateFeature)]);
        var payload = format switch
        {
            GeoParquetFormat => GeoParquetQueryFormatter.FormatAsGeoParquet(
                result, CreateResource(), returnGeometry: true, outputSrid: 4326,
                returnZ: true, returnM: false, new GeometryLimits()).response,
            GeoArrowFormat => (await GeoArrowQueryFormatter.FormatAsGeoArrowAsync(
                result, CreateResource(), returnGeometry: true, outputSrid: 4326,
                returnZ: true, returnM: false, new GeometryLimits())).response,
            _ => throw new ArgumentException($"Unknown probe format '{format}'.", nameof(args)),
        };

        await File.WriteAllBytesAsync(outputPath, payload);
        return 0;
    }

    /// <summary>Runs <see cref="Main"/> for <paramref name="format"/> in a reflection-disabled child.</summary>
    public static async Task<ProbeRun> RunAsync(string format)
    {
        var directory = Directory.CreateTempSubdirectory("honua-4747-");
        try
        {
            var testAssembly = typeof(ReflectionDisabledCloudNativeProbe).Assembly.Location;
            var runtimeConfig = JsonNode.Parse(
                await File.ReadAllTextAsync(Path.ChangeExtension(testAssembly, ".runtimeconfig.json")))!;
            var runtimeOptions = runtimeConfig["runtimeOptions"]!.AsObject();
            if (runtimeOptions["configProperties"] is not JsonObject configProperties)
            {
                configProperties = [];
                runtimeOptions["configProperties"] = configProperties;
            }

            configProperties[ReflectionSwitch] = false;
            var runtimeConfigPath = Path.Join(directory.FullName, "probe.runtimeconfig.json");
            await File.WriteAllTextAsync(runtimeConfigPath, runtimeConfig.ToJsonString());

            var outputPath = Path.Join(directory.FullName, "payload.bin");

            // Reuse the muxer hosting this test run rather than a PATH lookup, so a lane wrapper
            // around `dotnet` cannot rewrite the exec arguments.
            var host = Environment.ProcessPath is { } processPath
                && string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.Ordinal)
                    ? processPath
                    : "dotnet";
            var start = new ProcessStartInfo(host)
            {
                WorkingDirectory = directory.FullName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add("exec");
            start.ArgumentList.Add("--runtimeconfig");
            start.ArgumentList.Add(runtimeConfigPath);
            start.ArgumentList.Add("--depsfile");
            start.ArgumentList.Add(Path.ChangeExtension(testAssembly, ".deps.json"));
            start.ArgumentList.Add(testAssembly);
            start.ArgumentList.Add(format);
            start.ArgumentList.Add(outputPath);

            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }

            var payload = File.Exists(outputPath) ? await File.ReadAllBytesAsync(outputPath) : [];
            return new ProbeRun(process.ExitCode, await output, await errors, payload);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static Feature CreateFeature(ProbeRow row)
    {
        var point = row.Z is { } z
            ? new Point(new CoordinateZ(row.X, row.Y, z))
            : new Point(row.X, row.Y);
        var wkb = new WKBWriter(ByteOrder.LittleEndian, handleSRID: false, emitZ: row.Z is not null).Write(point);

        return Feature.Create(
            row.ObjectId,
            wkb,
            new Dictionary<string, object?>
            {
                ["objectid"] = row.ObjectId,
                ["name"] = row.Name,
                ["population"] = row.Population,
            }.ToImmutableDictionary());
    }

    private static MetadataV2Resource CreateResource()
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = "cng-features",
                Name = "cng_features",
                Description = "Reflection-disabled cloud-native probe layer",
            },
            SchemaFields =
            [
                new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.BigInteger, Nullable = false },
                new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String, Length = 255, Nullable = true },
                new MetadataV2Field { Name = "population", Type = MetadataV2FieldType.Integer, Nullable = true },
                new MetadataV2Field
                {
                    Name = "shape",
                    Type = MetadataV2FieldType.Geometry,
                    Nullable = true,
                    SemanticRoles = ["geometry.primary"],
                },
            ],
            Spatial = new MetadataV2ResourceSpatial
            {
                SpatialReference = MetadataV2SpatialReference.Wgs84,
                GeometryType = MetadataV2GeometryType.Point,
                PrimaryGeometryField = "shape",
            },
        };

    internal sealed record ProbeRow(long ObjectId, string Name, int? Population, double X, double Y, double? Z);

    internal sealed record ProbeRun(int ExitCode, string StandardOutput, string StandardError, byte[] Payload)
    {
        public string Transcript => $"exit {ExitCode}{Environment.NewLine}stdout:{Environment.NewLine}{StandardOutput}stderr:{Environment.NewLine}{StandardError}";
    }
}

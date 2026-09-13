// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace Honua.CloudIntegration.Tests;

/// <summary>
/// Exact-candidate replay of ImageServer mosaic native pixel size (honua-server#4562). Boots the published
/// server image named by <c>HONUA_CANDIDATE_IMAGE</c> in <c>Production</c> against a real PostGIS, registers
/// the Production raster trigger from the issue, and reads <c>GET /rest/services/{id}/ImageServer?f=json</c>.
/// </summary>
/// <remarks>
/// <para>
/// Layer <c>2400</c> is the trigger: two coextensive 4x4 EPSG:4326 rasters at different acquisition dates,
/// upper-left (-122.44, 37.78), scale (0.01, -0.01), two 32BF bands. Before #4563 the mosaic width rounded
/// from four to five columns and the service advertised <c>pixelSizeX=0.008000000000001251</c>. Layer
/// <c>2402</c> is the per-axis control: two 0.03 x 0.007-degree rasters, the second offset by one cell, so the
/// axes differ and the union extent is nonintegral in decimal degrees. The rasters stay grid-aligned: an
/// unaligned (mixed-resolution) mosaic fails service info with a 500 on every image before pixel size is
/// reached, tracked as honua-server#4792; <c>ImageServerMosaicPixelSizeTests</c> covers the mixed-resolution
/// pixel size contract until then.
/// </para>
/// <para>
/// The expected sizes are computed independently of the server: PostGIS reads each stored raster's
/// <c>ST_ScaleX</c>/<c>ST_ScaleY</c>, the per-axis minimum is taken, and it must equal the fixture constants
/// before the metadata is compared to it. Extent, band count, pixel type and spatial reference are checked
/// against the fixture as well.
/// </para>
/// <para>
/// Run it with <c>HONUA_CANDIDATE_IMAGE</c> set to the manifest-pinned image; it <c>[SkippableFact]</c>-skips
/// without it or without Docker. Pointing it at an image built before #4563 (for example
/// <c>ghcr.io/honua-io/honua-server:nightly-aot-9f855c0</c>, the Production source named in the issue) fails
/// the <c>pixelSizeX</c> assertion, which is the negative control for the replay. The JSON receipt records the
/// observed metadata before any assertion runs, so a failing image still leaves one; it is written to
/// <c>HONUA_CANDIDATE_RECEIPT_DIR</c> when set, and to the test output otherwise.
/// </para>
/// </remarks>
[Trait(CloudIntegrationTraits.Category, CloudIntegrationTraits.CandidateCertification)]
public sealed class CandidateImageServerPixelSizeCertificationTests
{
    private const string CandidateImageEnvVar = "HONUA_CANDIDATE_IMAGE";
    private const string ReceiptDirectoryEnvVar = "HONUA_CANDIDATE_RECEIPT_DIR";
    private const string PostgresImage = "postgis/postgis:16-3.4";
    private const string RedisImage = "redis:7-alpine";
    private const string DatabasePassword = "pixelsize_4562_pg";
    private const string AdminPassword = "PixelSize4562!Candidate#Key";
    private const int ReplicaContainerPort = 8080;
    private const double Tolerance = 1e-12;

    private static readonly LayerExpectation[] Layers =
    [
        new(2400, "Trigger Mosaic", PixelSizeX: 0.01, PixelSizeY: 0.01, XMin: -122.44, YMin: 37.74, XMax: -122.40, YMax: 37.78),
        new(2402, "Anisotropic Offset Mosaic", PixelSizeX: 0.03, PixelSizeY: 0.007, XMin: -122.44, YMin: 37.752, XMax: -122.29, YMax: 37.78)
    ];

    /// <summary>
    /// V1 catalog rows only; the server serves its own compilation of this catalog. Each layer is published by
    /// its own anonymous service and carries two 4x4 two-band Float32 rasters with distinct acquisition dates.
    /// </summary>
    private const string V1CatalogFixtureSql =
        """
        INSERT INTO honua.services (service_name, description, srid, supported_formats, capabilities, service_extent, metadata)
        VALUES
            ('native_raster_trigger', 'Two-date native raster mosaic', 4326, ARRAY['JSON'], ARRAY['Query'],
             ST_MakeEnvelope(-122.44, 37.74, -122.40, 37.78, 4326),
             jsonb_build_object('accessPolicy', jsonb_build_object('allowAnonymous', true))),
            ('native_raster_anisotropic', 'Anisotropic offset raster mosaic', 4326, ARRAY['JSON'], ARRAY['Query'],
             ST_MakeEnvelope(-122.44, 37.752, -122.29, 37.78, 4326),
             jsonb_build_object('accessPolicy', jsonb_build_object('allowAnonymous', true)));

        INSERT INTO honua.layers (layer_id, layer_name, description, table_name, geometry_type, srid, extent, metadata)
        VALUES
            (2400, 'Trigger Mosaic', 'Production raster trigger', 'features', 'Polygon', 4326,
             ST_MakeEnvelope(-122.44, 37.74, -122.40, 37.78, 4326),
             jsonb_build_object('accessPolicy', jsonb_build_object('allowAnonymous', true))),
            (2402, 'Anisotropic Offset Mosaic', 'Per-axis native resolution control', 'features', 'Polygon', 4326,
             ST_MakeEnvelope(-122.44, 37.752, -122.29, 37.78, 4326),
             jsonb_build_object('accessPolicy', jsonb_build_object('allowAnonymous', true)));

        INSERT INTO honua.service_layers (service_name, layer_id, layer_order)
        VALUES ('native_raster_trigger', 2400, 0), ('native_raster_anisotropic', 2402, 0);

        INSERT INTO honua.raster_data (layer_id, name, description, acquisition_date, raster)
        SELECT layer_id, name, 'two-band 4x4 Float32 fixture', acquisition_date::timestamptz,
               ST_AddBand(
                   ST_AddBand(ST_MakeEmptyRaster(4, 4, upper_left_x, upper_left_y, scale_x, scale_y, 0, 0, 4326),
                              1, '32BF', band1, -9999),
                   2, '32BF', band2, -9999)
        FROM (VALUES
                (2400, 'trigger-2026-01-01', '2026-01-01T00:00:00Z', -122.44, 37.78, 0.01, -0.01, 1022, 1122),
                (2400, 'trigger-2026-02-01', '2026-02-01T00:00:00Z', -122.44, 37.78, 0.01, -0.01, 2022, 2122),
                (2402, 'anisotropic-origin', '2026-01-01T00:00:00Z', -122.44, 37.78, 0.03, -0.007, 3022, 3122),
                (2402, 'anisotropic-offset', '2026-02-01T00:00:00Z', -122.41, 37.78, 0.03, -0.007, 4022, 4122))
            AS fixture(layer_id, name, acquisition_date, upper_left_x, upper_left_y, scale_x, scale_y, band1, band2);
        """;

    private readonly ITestOutputHelper _output;

    public CandidateImageServerPixelSizeCertificationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SkippableFact]
    public async Task MosaicServiceInfo_AdvertisesFinestNativePixelSizePerAxis()
    {
        var candidateReference = Environment.GetEnvironmentVariable(CandidateImageEnvVar);
        Skip.If(string.IsNullOrWhiteSpace(candidateReference), $"Set {CandidateImageEnvVar} to run the exact-candidate ImageServer pixel size replay.");
        Skip.IfNot((await Docker.RunAsync(["version", "--format", "{{.Server.Version}}"])).ExitCode == 0, "Docker is not available.");

        var image = await Docker.ResolveImageAsync(candidateReference!);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var network = $"honua-px4562-{suffix}";
        var postgres = $"{network}-pg";
        var redis = $"{network}-redis";
        var server = $"{network}-server";
        var port = LocalSubstrateDockerFixture.GetFreeTcpPort();
        var baseUrl = $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}";
        var passed = false;

        try
        {
            await Docker.RunCheckedAsync(["network", "create", network]);
            await Docker.RunCheckedAsync(
            [
                "run", "-d", "--name", postgres, "--network", network,
                "-e", "POSTGRES_USER=postgres", "-e", $"POSTGRES_PASSWORD={DatabasePassword}", "-e", "POSTGRES_DB=honua",
                PostgresImage
            ]);
            await WaitForPostgresAsync(postgres);
            await PsqlAsync(
                postgres,
                "CREATE EXTENSION IF NOT EXISTS postgis; CREATE EXTENSION IF NOT EXISTS postgis_raster; " +
                "CREATE EXTENSION IF NOT EXISTS unaccent; CREATE EXTENSION IF NOT EXISTS pgcrypto;");
            await Docker.RunCheckedAsync(["run", "-d", "--name", redis, "--network", network, RedisImage]);

            await Docker.RunCheckedAsync(
            [
                "run", "-d", "--name", server, "--network", network,
                "-p", $"127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}:{ReplicaContainerPort.ToString(CultureInfo.InvariantCulture)}",
                "-e", "ASPNETCORE_ENVIRONMENT=Production",
                "-e", $"PUBLIC_BASE_URL={baseUrl}",
                "-e", $"ConnectionStrings__DefaultConnection=Host={postgres};Database=honua;Username=postgres;Password={DatabasePassword}",
                "-e", $"ConnectionStrings__Redis={redis}:6379",
                "-e", $"HONUA_ADMIN_PASSWORD={AdminPassword}",
                "-e", "HostValidation__AllowedHosts__0=127.0.0.1",
                "-e", "HostValidation__AllowedHosts__1=localhost",
                "-e", "Security__ConnectionEncryption__MasterKey=pixelsize-4562-candidate-master-key-0123456789abcdef",
                "-e", "Security__ConnectionEncryption__Salt=cGl4ZWxzaXplLTQ1NjItY2FuZGlkYXRlLXNhbHQ=",
                image.Reference
            ]);

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

            // First boot runs the migrations the fixture writes into.
            (await WaitForReadyAsync(client, baseUrl)).Should().BeTrue("the candidate must become ready on a fresh database");

            await PsqlAsync(postgres, V1CatalogFixtureSql);

            // A fresh deployment activates an empty snapshot while the catalog is still empty. Remove only that
            // (asserted empty) activation so the server compiles the V1 catalog it now holds.
            (await PsqlScalarAsync(
                    postgres,
                    "SELECT COALESCE(SUM(jsonb_array_length(s.document -> 'publications')), 0) " +
                    "FROM honua.metadata_v2_current c JOIN honua.metadata_v2_snapshots s USING (environment, revision)"))
                .Should().Be("0", "only an empty boot-time activation may be replaced by the V1 catalog compilation");
            await PsqlAsync(postgres, "DELETE FROM honua.metadata_v2_current; DELETE FROM honua.metadata_v2_snapshots;");
            await Docker.RunCheckedAsync(["restart", server]);
            (await WaitForReadyAsync(client, baseUrl)).Should().BeTrue("the candidate must restart ready over the seeded catalog");

            var observations = new List<LayerObservation>();
            foreach (var layer in Layers)
            {
                observations.Add(await ObserveLayerAsync(client, baseUrl, postgres, layer));
            }

            // Receipt first: a pre-fix image fails below and must still leave the values it advertised.
            await WriteReceiptAsync(image, observations);

            foreach (var observation in observations)
            {
                AssertLayer(observation);
            }

            passed = true;
        }
        finally
        {
            if (!passed)
            {
                var (_, logs, errors) = await Docker.RunAsync(["logs", "--tail", "60", server]);
                _output.WriteLine(logs + errors);
            }

            foreach (var container in new[] { server, redis, postgres })
            {
                await Docker.RunAsync(["rm", "-f", container]);
            }

            await Docker.RunAsync(["network", "rm", network]);
        }
    }

    private static async Task<LayerObservation> ObserveLayerAsync(HttpClient client, string baseUrl, string postgres, LayerExpectation layer)
    {
        var id = layer.LayerId.ToString(CultureInfo.InvariantCulture);

        // Independent oracle: the stored rasters' own geotransforms, read by PostGIS rather than the server.
        var stored = (await PsqlScalarAsync(
                postgres,
                "SELECT count(*) || '|' || count(DISTINCT acquisition_date) || '|' || min(ST_Width(raster)) || '|' || max(ST_Width(raster)) || '|' || " +
                "min(ST_Height(raster)) || '|' || max(ST_Height(raster)) || '|' || " +
                "to_char(min(ST_ScaleX(raster)), 'FM0.999999999999') || '|' || to_char(min(abs(ST_ScaleY(raster))), 'FM0.999999999999') " +
                $"FROM honua.raster_data WHERE layer_id = {id}"))
            .Split('|');

        using var response = await client.GetAsync($"{baseUrl}/rest/services/{id}/ImageServer?f=json");
        var body = await response.Content.ReadAsStringAsync();
        var observation = new LayerObservation(
            layer,
            RasterCount: int.Parse(stored[0], CultureInfo.InvariantCulture),
            AcquisitionDates: int.Parse(stored[1], CultureInfo.InvariantCulture),
            StoredWidths: [int.Parse(stored[2], CultureInfo.InvariantCulture), int.Parse(stored[3], CultureInfo.InvariantCulture)],
            StoredHeights: [int.Parse(stored[4], CultureInfo.InvariantCulture), int.Parse(stored[5], CultureInfo.InvariantCulture)],
            StoredMinScaleX: double.Parse(stored[6], CultureInfo.InvariantCulture),
            StoredMinScaleY: double.Parse(stored[7], CultureInfo.InvariantCulture),
            Status: (int)response.StatusCode,
            Error: null,
            PixelSizeX: double.NaN,
            PixelSizeY: double.NaN,
            BandCount: 0,
            PixelType: null,
            Wkid: 0,
            Extent: []);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (response.StatusCode != HttpStatusCode.OK || root.TryGetProperty("error", out _))
        {
            // Kept for the receipt; AssertLayer reports it.
            return observation with { Error = body };
        }

        var extent = root.GetProperty("extent");
        return observation with
        {
            PixelSizeX = root.GetProperty("pixelSizeX").GetDouble(),
            PixelSizeY = root.GetProperty("pixelSizeY").GetDouble(),
            BandCount = root.GetProperty("bandCount").GetInt32(),
            PixelType = root.GetProperty("pixelType").GetString(),
            Wkid = root.GetProperty("spatialReference").GetProperty("wkid").GetInt32(),
            Extent =
            [
                extent.GetProperty("xmin").GetDouble(),
                extent.GetProperty("ymin").GetDouble(),
                extent.GetProperty("xmax").GetDouble(),
                extent.GetProperty("ymax").GetDouble()
            ]
        };
    }

    private static void AssertLayer(LayerObservation observation)
    {
        var layer = observation.Layer;
        var because = $"layer {layer.LayerId.ToString(CultureInfo.InvariantCulture)} ({layer.Title})";

        // The oracle must agree with the fixture before the server is compared to it.
        observation.RasterCount.Should().Be(2, because);
        observation.AcquisitionDates.Should().Be(2, because);
        observation.StoredWidths.Should().Equal([4, 4], because);
        observation.StoredHeights.Should().Equal([4, 4], because);
        observation.StoredMinScaleX.Should().BeApproximately(layer.PixelSizeX, Tolerance, because);
        observation.StoredMinScaleY.Should().BeApproximately(layer.PixelSizeY, Tolerance, because);

        observation.Status.Should().Be((int)HttpStatusCode.OK, $"{because} service info: {observation.Error}");
        observation.Error.Should().BeNull(because);
        observation.PixelSizeX.Should().BeApproximately(observation.StoredMinScaleX, Tolerance, $"{because} advertises its finest native x cell size");
        observation.PixelSizeY.Should().BeApproximately(observation.StoredMinScaleY, Tolerance, $"{because} advertises its finest native y cell size");
        observation.BandCount.Should().Be(2, because);
        observation.PixelType.Should().Be("F32", because);
        observation.Wkid.Should().Be(4326, because);
        observation.Extent[0].Should().BeApproximately(layer.XMin, 1e-9, $"{because} xmin");
        observation.Extent[1].Should().BeApproximately(layer.YMin, 1e-9, $"{because} ymin");
        observation.Extent[2].Should().BeApproximately(layer.XMax, 1e-9, $"{because} xmax");
        observation.Extent[3].Should().BeApproximately(layer.YMax, 1e-9, $"{because} ymax");
    }

    private static async Task<bool> WaitForReadyAsync(HttpClient client, string baseUrl)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(3);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var response = await client.GetAsync($"{baseUrl}/healthz/ready");
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return true;
                }
            }
            catch (HttpRequestException)
            {
                // The container is still starting or restarting.
            }
            catch (TaskCanceledException)
            {
                // A request that outlives the client timeout during startup.
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        return false;
    }

    private static async Task WaitForPostgresAsync(string name)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            // pg_isready over TCP: the image's init phase answers on the unix socket before the final server.
            var (exitCode, _, _) = await Docker.RunAsync(["exec", name, "pg_isready", "-h", "127.0.0.1", "-U", "postgres", "-d", "honua"]);
            if (exitCode == 0)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        throw new InvalidOperationException($"PostGIS container '{name}' did not become ready.");
    }

    private static Task PsqlAsync(string container, string sql)
        => Docker.RunCheckedAsync(["exec", container, "psql", "-h", "127.0.0.1", "-U", "postgres", "-d", "honua", "-v", "ON_ERROR_STOP=1", "-q", "-c", sql]);

    private static async Task<string> PsqlScalarAsync(string container, string sql)
    {
        var (exitCode, stdout, stderr) = await Docker.RunAsync(
            ["exec", container, "psql", "-h", "127.0.0.1", "-U", "postgres", "-d", "honua", "-v", "ON_ERROR_STOP=1", "-tA", "-c", sql]);
        exitCode.Should().Be(0, stderr);
        return stdout.Trim();
    }

    private async Task WriteReceiptAsync(ImageIdentity image, IReadOnlyList<LayerObservation> observations)
    {
        var receipt = JsonSerializer.Serialize(
            new Dictionary<string, object?>
            {
                ["issue"] = "honua-io/honua-server#4562",
                ["observedAt"] = DateTimeOffset.UtcNow,
                ["image"] = image.Reference,
                ["imageId"] = image.Id,
                ["repoDigests"] = image.RepoDigests,
                ["revision"] = image.Revision,
                ["environment"] = "Production",
                ["layers"] = observations.Select(observation => new Dictionary<string, object?>
                {
                    ["layerId"] = observation.Layer.LayerId,
                    ["title"] = observation.Layer.Title,
                    ["storedRasterCount"] = observation.RasterCount,
                    ["storedAcquisitionDates"] = observation.AcquisitionDates,
                    ["storedMinScaleX"] = observation.StoredMinScaleX,
                    ["storedMinAbsScaleY"] = observation.StoredMinScaleY,
                    ["expectedPixelSizeX"] = observation.Layer.PixelSizeX,
                    ["expectedPixelSizeY"] = observation.Layer.PixelSizeY,
                    ["status"] = observation.Status,
                    ["error"] = observation.Error,
                    ["pixelSizeX"] = double.IsNaN(observation.PixelSizeX) ? null : observation.PixelSizeX,
                    ["pixelSizeY"] = double.IsNaN(observation.PixelSizeY) ? null : observation.PixelSizeY,
                    ["bandCount"] = observation.BandCount,
                    ["pixelType"] = observation.PixelType,
                    ["wkid"] = observation.Wkid,
                    ["extent"] = observation.Extent
                }).ToArray()
            },
            new JsonSerializerOptions { WriteIndented = true });

        _output.WriteLine(receipt);
        var directory = Environment.GetEnvironmentVariable(ReceiptDirectoryEnvVar);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Join(directory, "imageserver-mosaic-pixel-size.json"), receipt);
    }

    private sealed record LayerExpectation(
        int LayerId, string Title, double PixelSizeX, double PixelSizeY, double XMin, double YMin, double XMax, double YMax);

    private sealed record LayerObservation(
        LayerExpectation Layer,
        int RasterCount,
        int AcquisitionDates,
        int[] StoredWidths,
        int[] StoredHeights,
        double StoredMinScaleX,
        double StoredMinScaleY,
        int Status,
        string? Error,
        double PixelSizeX,
        double PixelSizeY,
        int BandCount,
        string? PixelType,
        int Wkid,
        double[] Extent);

    private sealed record ImageIdentity(string Reference, string Id, IReadOnlyList<string> RepoDigests, string Revision);

    private static class Docker
    {
        public static async Task<ImageIdentity> ResolveImageAsync(string reference)
        {
            const string Format = "{{.Id}}|{{join .RepoDigests \",\"}}|{{index .Config.Labels \"org.opencontainers.image.revision\"}}";
            var (exitCode, stdout, _) = await RunAsync(["image", "inspect", "-f", Format, reference]);
            if (exitCode != 0)
            {
                await RunCheckedAsync(["pull", "-q", reference]);
                (exitCode, stdout, _) = await RunAsync(["image", "inspect", "-f", Format, reference]);
            }

            exitCode.Should().Be(0, $"image '{reference}' must be resolvable to an immutable id");
            var parts = stdout.Trim().Split('|');
            return new ImageIdentity(
                reference,
                parts[0],
                parts.Length > 1 ? parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries) : [],
                parts.Length > 2 ? parts[2] : string.Empty);
        }

        public static async Task RunCheckedAsync(IReadOnlyList<string> arguments)
        {
            var (exitCode, _, stderr) = await RunAsync(arguments);
            if (exitCode != 0)
            {
                throw new InvalidOperationException($"docker {arguments[0]} failed (exit {exitCode.ToString(CultureInfo.InvariantCulture)}): {stderr.Trim()}");
            }
        }

        public static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunAsync(IReadOnlyList<string> arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "docker",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = new Process { StartInfo = startInfo };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    stdout.AppendLine(e.Data);
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    stderr.AppendLine(e.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();
            return (process.ExitCode, stdout.ToString(), stderr.ToString());
        }
    }
}

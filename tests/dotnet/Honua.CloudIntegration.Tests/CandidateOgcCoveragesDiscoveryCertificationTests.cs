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
/// Exact-candidate replay of OGC API Coverages discovery identity (honua-server#4564). Boots the published
/// server image named by <c>HONUA_CANDIDATE_IMAGE</c> in <c>Production</c> against a real PostGIS, registers
/// one public and one protected raster layer in the V1 catalog, and serves them through the server's own
/// canonical V1-to-Metadata-v2 compilation. That compilation emits a feature resource
/// (<c>res-layer-N</c>) and a raster resource (<c>res-image-layer-N</c>) sharing storage layer <c>N</c>, each
/// on a service that enables OGC API Coverages: the shape that made discovery advertise <c>2400</c> twice.
/// </summary>
/// <remarks>
/// <para>
/// The expected identities are fixed by the fixture, not read back from the server: anonymous discovery lists
/// exactly <c>2400</c>, an administrator sees <c>2400</c> and <c>2401</c> once each, every advertised self
/// link resolves to the same identity and title, and the protected collection and its coverage answer 401
/// anonymously and 200 with the administrator key.
/// </para>
/// <para>
/// Run it with <c>HONUA_CANDIDATE_IMAGE</c> set to the manifest-pinned image; it <c>[SkippableFact]</c>-skips
/// without it or without Docker. Pointing it at an image built before #4567 (for example
/// <c>ghcr.io/honua-io/honua-server:nightly-aot-9f855c0</c>) fails the unique-identity assertion, which is
/// the negative control for the replay. A JSON receipt is written to <c>HONUA_CANDIDATE_RECEIPT_DIR</c> when
/// it is set, and to the test output otherwise.
/// </para>
/// </remarks>
[Trait(CloudIntegrationTraits.Category, CloudIntegrationTraits.CandidateCertification)]
public sealed class CandidateOgcCoveragesDiscoveryCertificationTests
{
    private const string CandidateImageEnvVar = "HONUA_CANDIDATE_IMAGE";
    private const string ReceiptDirectoryEnvVar = "HONUA_CANDIDATE_RECEIPT_DIR";
    private const string PostgresImage = "postgis/postgis:16-3.4";
    private const string RedisImage = "redis:7-alpine";
    private const string DatabasePassword = "coverages_4564_pg";
    private const string AdminPassword = "Coverages4564!Candidate#Key";
    private const int ReplicaContainerPort = 8080;
    private const string PublicCollectionId = "2400";
    private const string ProtectedCollectionId = "2401";
    private const string PublicTitle = "Public Gradient";
    private const string ProtectedTitle = "Protected Gradient";

    /// <summary>
    /// V1 catalog rows only; no Metadata v2 snapshot is written, so the server serves its own compilation of
    /// this catalog. Both layers share the <c>features</c> table and carry a 4x4 two-band Float32 raster.
    /// </summary>
    private const string V1CatalogFixtureSql =
        """
        INSERT INTO honua.services (service_name, description, srid, supported_formats, capabilities, service_extent, metadata)
        VALUES
            ('native_raster_public', 'Public raster coverage', 4326, ARRAY['JSON'], ARRAY['Query'],
             ST_MakeEnvelope(-122.44, 37.74, -122.40, 37.78, 4326),
             jsonb_build_object('accessPolicy', jsonb_build_object('allowAnonymous', true))),
            ('native_raster_protected', 'Protected raster coverage', 4326, ARRAY['JSON'], ARRAY['Query'],
             ST_MakeEnvelope(-122.44, 37.74, -122.40, 37.78, 4326),
             jsonb_build_object('accessPolicy', jsonb_build_object('allowAnonymous', false)));

        INSERT INTO honua.layers (layer_id, layer_name, description, table_name, geometry_type, srid, extent, metadata)
        VALUES
            (2400, 'Public Gradient', 'Public raster layer', 'features', 'Polygon', 4326,
             ST_MakeEnvelope(-122.44, 37.74, -122.40, 37.78, 4326),
             jsonb_build_object('accessPolicy', jsonb_build_object('allowAnonymous', true))),
            (2401, 'Protected Gradient', 'Protected raster layer', 'features', 'Polygon', 4326,
             ST_MakeEnvelope(-122.44, 37.74, -122.40, 37.78, 4326),
             jsonb_build_object('accessPolicy', jsonb_build_object('allowAnonymous', false)));

        INSERT INTO honua.service_layers (service_name, layer_id, layer_order)
        VALUES ('native_raster_public', 2400, 0), ('native_raster_protected', 2401, 0);

        INSERT INTO honua.raster_data (layer_id, name, description, raster)
        SELECT layer_id, 'gradient', 'two-band 4x4 Float32 fixture',
               ST_AddBand(
                   ST_AddBand(ST_MakeEmptyRaster(4, 4, -122.44, 37.78, 0.01, -0.01, 0, 0, 4326),
                              1, '32BF', 1022, -9999),
                   2, '32BF', 1122, -9999)
        FROM (VALUES (2400), (2401)) AS fixture(layer_id);
        """;

    private readonly ITestOutputHelper _output;

    public CandidateOgcCoveragesDiscoveryCertificationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SkippableFact]
    public async Task SharedStorageAliases_DiscoverOneCollectionPerRoute_AndProtectedPublicationStaysProtected()
    {
        var candidateReference = Environment.GetEnvironmentVariable(CandidateImageEnvVar);
        Skip.If(string.IsNullOrWhiteSpace(candidateReference), $"Set {CandidateImageEnvVar} to run the exact-candidate coverages discovery replay.");
        Skip.IfNot((await Docker.RunAsync(["version", "--format", "{{.Server.Version}}"])).ExitCode == 0, "Docker is not available.");

        var image = await Docker.ResolveImageAsync(candidateReference!);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var network = $"honua-cov4564-{suffix}";
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
                "-e", "Security__ConnectionEncryption__MasterKey=coverages-4564-candidate-master-key-0123456789abcdef",
                "-e", "Security__ConnectionEncryption__Salt=Y292ZXJhZ2VzLTQ1NjQtY2FuZGlkYXRlLXNhbHQ=",
                image.Reference
            ]);

            using var anonymous = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            using var admin = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            admin.DefaultRequestHeaders.Add("X-API-Key", AdminPassword);

            // First boot runs the migrations the fixture writes into.
            (await WaitForReadyAsync(anonymous, baseUrl)).Should().BeTrue("the candidate must become ready on a fresh database");

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
            (await WaitForReadyAsync(anonymous, baseUrl)).Should().BeTrue("the candidate must restart ready over the seeded catalog");
            (await PsqlScalarAsync(postgres, "SELECT count(*) FROM honua.metadata_v2_current"))
                .Should().Be("0", "the served graph is the server's compilation of the V1 catalog, not an activated snapshot");

            // Precondition: the feature alias and the raster alias of storage layer 2400 are both routable.
            using (var featureLayer = await GetJsonAsync(anonymous, $"{baseUrl}/rest/services/native_raster_public/FeatureServer/2400?f=json", HttpStatusCode.OK))
            {
                featureLayer.RootElement.GetProperty("id").GetInt32().Should().Be(2400);
                featureLayer.RootElement.GetProperty("type").GetString().Should().Be("Feature Layer");
            }

            using (var imageService = await GetJsonAsync(anonymous, $"{baseUrl}/rest/services/native_raster_public/ImageServer?f=json", HttpStatusCode.OK))
            {
                imageService.RootElement.TryGetProperty("error", out _).Should().BeFalse();
                imageService.RootElement.GetProperty("name").GetString().Should().Be(PublicTitle);
            }

            var anonymousIds = await AssertDiscoveryAsync(anonymous, baseUrl, new Dictionary<string, string>
            {
                [PublicCollectionId] = PublicTitle
            });
            var adminIds = await AssertDiscoveryAsync(admin, baseUrl, new Dictionary<string, string>
            {
                [PublicCollectionId] = PublicTitle,
                [ProtectedCollectionId] = ProtectedTitle
            });

            var accessChecks = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var path in new[]
                     {
                         $"/ogc/coverages/collections/{ProtectedCollectionId}",
                         $"/ogc/coverages/collections/{ProtectedCollectionId}/coverage"
                     })
            {
                using var denied = await anonymous.GetAsync(baseUrl + path);
                using var allowed = await admin.GetAsync(baseUrl + path);
                accessChecks[$"anonymous {path}"] = (int)denied.StatusCode;
                accessChecks[$"admin {path}"] = (int)allowed.StatusCode;
                denied.StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"anonymous {path} targets a protected publication");
                allowed.StatusCode.Should().Be(HttpStatusCode.OK, $"the administrator key may read {path}");
            }

            using (var publicCoverage = await anonymous.GetAsync($"{baseUrl}/ogc/coverages/collections/{PublicCollectionId}/coverage"))
            {
                accessChecks[$"anonymous /ogc/coverages/collections/{PublicCollectionId}/coverage"] = (int)publicCoverage.StatusCode;
                publicCoverage.StatusCode.Should().Be(HttpStatusCode.OK);
            }

            await WriteReceiptAsync(image, anonymousIds, adminIds, accessChecks);
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

    /// <summary>
    /// Asserts discovery lists exactly <paramref name="expected"/> once each and that every advertised self link
    /// resolves to the same identity and title. Returns the advertised identifiers in response order.
    /// </summary>
    private static async Task<string[]> AssertDiscoveryAsync(HttpClient client, string baseUrl, IReadOnlyDictionary<string, string> expected)
    {
        using var document = await GetJsonAsync(client, $"{baseUrl}/ogc/coverages/collections", HttpStatusCode.OK);
        var collections = document.RootElement.GetProperty("collections").EnumerateArray().ToArray();
        var ids = collections.Select(collection => collection.GetProperty("id").GetString()!).ToArray();

        ids.Should().OnlyHaveUniqueItems("each collection route identifier is advertised once");
        ids.Should().BeEquivalentTo(expected.Keys);

        foreach (var collection in collections)
        {
            var id = collection.GetProperty("id").GetString()!;
            collection.GetProperty("title").GetString().Should().Be(expected[id]);
            var self = collection.GetProperty("links").EnumerateArray()
                .Single(link => link.GetProperty("rel").GetString() == "self")
                .GetProperty("href").GetString();
            self.Should().Be($"{baseUrl}/ogc/coverages/collections/{id}");

            using var detail = await GetJsonAsync(client, self!, HttpStatusCode.OK);
            detail.RootElement.GetProperty("id").GetString().Should().Be(id);
            detail.RootElement.GetProperty("title").GetString().Should().Be(expected[id]);
        }

        return ids;
    }

    private static async Task<JsonDocument> GetJsonAsync(HttpClient client, string url, HttpStatusCode expectedStatus)
    {
        using var response = await client.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(expectedStatus, $"GET {url}: {body}");
        return JsonDocument.Parse(body);
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

    private async Task WriteReceiptAsync(
        ImageIdentity image,
        string[] anonymousIds,
        string[] adminIds,
        IReadOnlyDictionary<string, int> accessChecks)
    {
        var receipt = JsonSerializer.Serialize(
            new Dictionary<string, object?>
            {
                ["issue"] = "honua-io/honua-server#4564",
                ["observedAt"] = DateTimeOffset.UtcNow,
                ["image"] = image.Reference,
                ["imageId"] = image.Id,
                ["repoDigests"] = image.RepoDigests,
                ["revision"] = image.Revision,
                ["environment"] = "Production",
                ["anonymousCollectionIds"] = anonymousIds,
                ["adminCollectionIds"] = adminIds,
                ["accessChecks"] = accessChecks
            },
            new JsonSerializerOptions { WriteIndented = true });

        var directory = Environment.GetEnvironmentVariable(ReceiptDirectoryEnvVar);
        if (string.IsNullOrWhiteSpace(directory))
        {
            _output.WriteLine(receipt);
            return;
        }

        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Join(directory, "ogc-coverages-discovery-identity.json"), receipt);
    }

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

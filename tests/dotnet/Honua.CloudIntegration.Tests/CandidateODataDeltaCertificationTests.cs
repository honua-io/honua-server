// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Npgsql;
using Xunit;
using Xunit.Abstractions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Honua.CloudIntegration.Tests;

/// <summary>
/// Exact-candidate OData lossless-delta certification (honua-server#3872). Boots the real,
/// digest-addressed candidate server image (<c>HONUA_CANDIDATE_IMAGE</c>) against a real PostGIS
/// over the production container path, drives the equal-timestamp / delete / recreate /
/// filter-transition delta scenario over real HTTP, restarts the candidate container mid-scenario,
/// and asserts convergence against an independently computed direct SQL query — never in-process.
/// </summary>
/// <remarks>
/// <para>
/// Requires Docker and <c>HONUA_CANDIDATE_IMAGE</c>; the test <c>[SkippableFact]</c>-skips when
/// either is unavailable, mirroring <see cref="CandidateTelemetryGateCertificationTests"/>. Writes
/// an immutable JSON receipt (image identity, per-stage state hashes, and the pass/fail outcome) to
/// <c>HONUA_CANDIDATE_RECEIPT_DIR</c> when it is set, and to the test output otherwise.
/// </para>
/// <para>
/// The candidate image boots against an empty database and immediately activates a Metadata v2
/// snapshot containing only the built-in GPServer bootstrap entry (honua-server#2349); the
/// read path never re-derives one from the legacy V1 catalog once any snapshot is active
/// (honua-server#1412). This harness seeds the V1 catalog directly
/// (<c>tests/seed/odata.yaml</c>'s SQL, byte-identical to the in-process fixture) and then
/// activates the same V1-to-V2 compat projection production code uses on a real publish
/// (<c>PostgresMetadataV2LegacyCatalogProjector</c> / <c>CloudDemoServiceSeeder</c>) — executed
/// here as the equivalent SQL function the shared test fixture already ships
/// (<c>tests/seed/base-schema.sql</c>'s <c>honua.seed_metadata_v2_compat_snapshot()</c>), applied
/// against the target database once the candidate's own migrations have created the schema, so
/// the next request already resolves the seeded layer through the real, unmodified read path
/// exactly as it would after a real publish, with no restart or cache-invalidation race observed.
/// </para>
/// </remarks>
[Trait(CloudIntegrationTraits.Category, CloudIntegrationTraits.CandidateCertification)]
public sealed class CandidateODataDeltaCertificationTests
{
    private const string CandidateImageEnvVar = "HONUA_CANDIDATE_IMAGE";
    private const string ReceiptDirectoryEnvVar = "HONUA_CANDIDATE_RECEIPT_DIR";
    private const int TestLayerId = 0;

    private readonly ITestOutputHelper _output;

    public CandidateODataDeltaCertificationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SkippableFact]
    public async Task ExactCandidate_EqualTimestampsDeletesRecreateFilterTransitionsAndRestart_Converge()
    {
        var imageReference = Environment.GetEnvironmentVariable(CandidateImageEnvVar);
        Skip.If(string.IsNullOrWhiteSpace(imageReference), $"Set {CandidateImageEnvVar} to run the exact-candidate OData delta lane.");
        Skip.IfNot(await Docker.IsAvailableAsync(), "Docker is not available for the exact-candidate OData delta lane.");

        var image = await Docker.ResolveImageAsync(imageReference!);
        await using var env = await CandidateODataEnvironment.StartAsync(image, _output);

        var state = new Dictionary<long, string>();

        async Task<Dictionary<long, string>> AuthoritativeAsync()
        {
            var rows = new Dictionary<long, string>();
            await using var connection = await env.OpenPostgresConnectionAsync();
            await using var command = new NpgsqlCommand(
                "SELECT objectid, attributes->>'name' FROM honua.features " +
                "WHERE layer_id = 0 AND attributes->>'name' <> 'excluded' ORDER BY objectid;",
                connection);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                rows.Add(reader.GetInt64(0), reader.GetString(1));
            }

            return rows;
        }

        async Task<string> FollowAsync(string link, bool baseline)
        {
            var pages = 0;
            while (true)
            {
                // The baseline set carries the odata.yaml fixture's 15 pre-existing cities plus this
                // scenario's own rows, so the bound must clear that count with headroom, not just
                // the scenario's own six rows.
                (++pages).Should().BeLessThan(40, "paging must terminate without unbounded duplicates");
                // The candidate advertises links with its own container-internal host; only the
                // path and query are stable across the real front door this harness talks to.
                var pathAndQuery = link.StartsWith('/') ? link : new Uri(link).PathAndQuery;
                using var request = new HttpRequestMessage(HttpMethod.Get, pathAndQuery);
                if (baseline)
                {
                    request.Headers.TryAddWithoutValidation("Prefer", "odata.track-changes");
                }

                using var response = await env.HttpClient.SendAsync(request);
                response.StatusCode.Should().Be(HttpStatusCode.OK, "request {0} must succeed against the real candidate", request.RequestUri);
                using var page = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                page.RootElement.GetProperty("@odata.context").GetString().Should()
                    .EndWith(baseline ? "#Features" : "#Features/$delta");
                var values = page.RootElement.GetProperty("value").EnumerateArray().ToArray();
                values.Length.Should().BeLessThanOrEqualTo(1);
                foreach (var value in values)
                {
                    var id = value.GetProperty("ObjectId").GetInt64();
                    if (value.TryGetProperty("@removed", out var removed))
                    {
                        removed.GetProperty("reason").GetString().Should().BeOneOf("deleted", "changed");
                        state.Remove(id);
                    }
                    else
                    {
                        state[id] = value.GetProperty("name").GetString()!;
                        value.GetProperty("LayerId").GetInt32().Should().Be(0);
                        var geometry = value.GetProperty("Geometry");
                        if (id is 73001 or 73002)
                        {
                            geometry.GetProperty("type").GetString().Should().Be("Point");
                            var expectedPoint = id == 73001 ? new[] { 1.0, 2.0 }
                                : baseline ? new[] { 2.0, 3.0 } : new[] { 7.0, 11.0 };
                            geometry.GetProperty("coordinates").EnumerateArray().Select(item => item.GetDouble())
                                .Should().Equal(expectedPoint, "longitude/latitude ordinates are independently specified by the SQL fixture");
                        }
                        else if (id is >= 73000 and <= 73006)
                        {
                            // The scenario's own null-geometry rows (73003/73004/73005/73006); the
                            // pre-existing odata.yaml baseline cities (1-15) keep their real points
                            // and are not part of this scenario's geometry assertions.
                            geometry.ValueKind.Should().Be(JsonValueKind.Null);
                        }
                    }
                }

                if (page.RootElement.TryGetProperty("@odata.nextLink", out var next))
                {
                    link = next.GetString()!;
                    continue;
                }

                return page.RootElement.GetProperty("@odata.deltaLink").GetString()!;
            }
        }

        await env.SeedScenarioRowsAsync();

        var delta = await FollowAsync($"/odata/Features({TestLayerId})?$filter=name%20ne%20'excluded'&$top=1", true);
        var baselineExpected = new Dictionary<long, string>
        {
            [73001] = "first",
            [73002] = "second",
            [73003] = "leaving",
            [73004] = "recreate",
            [73006] = "delete"
        };
        state.Where(entry => entry.Key >= 73000).Should().BeEquivalentTo(baselineExpected);

        await env.MutateScenarioRowsAsync();

        var terminal = await FollowAsync(delta, false);
        var expected = new Dictionary<long, string>
        {
            [73001] = "first-updated",
            [73002] = "second-updated",
            [73004] = "recreated",
            [73005] = "entered"
        };
        state.Where(entry => entry.Key >= 73000).Should().BeEquivalentTo(expected, "the independently specified mutation outcome must replace the baseline");
        var afterMutation = state.Where(entry => entry.Key >= 73000).ToDictionary(entry => entry.Key, entry => entry.Value);
        StateHash(afterMutation).Should().Be(StateHash(expected));
        var authoritative = (await AuthoritativeAsync()).Where(entry => entry.Key >= 73000).ToDictionary(entry => entry.Key, entry => entry.Value);
        StateHash(afterMutation).Should().Be(StateHash(authoritative), "the subscriber converges to an independent direct SQL query against the real candidate database");

        _ = await FollowAsync(terminal, false);
        state.Where(entry => entry.Key >= 73000).Should().BeEquivalentTo(expected, "terminal polling is idempotent");

        await env.RestartCandidateAsync();

        _ = await FollowAsync(terminal, false);
        state.Where(entry => entry.Key >= 73000).Should().BeEquivalentTo(expected, "the same durable terminal token survives a complete container restart");

        await env.ApplyPostRestartMutationAsync();
        expected[73001] = "after-restart";
        var restartedTerminal = await FollowAsync(terminal, false);
        state.Where(entry => entry.Key >= 73000).Should().BeEquivalentTo(expected, "a post-restart update at the same timestamp must converge without rebaselining");
        var afterRestart = state.Where(entry => entry.Key >= 73000).ToDictionary(entry => entry.Key, entry => entry.Value);
        StateHash(afterRestart).Should().Be(StateHash(expected));
        var authoritativeAfterRestart = (await AuthoritativeAsync()).Where(entry => entry.Key >= 73000).ToDictionary(entry => entry.Key, entry => entry.Value);
        StateHash(afterRestart).Should().Be(StateHash(authoritativeAfterRestart));
        _ = await FollowAsync(restartedTerminal, false);
        state.Where(entry => entry.Key >= 73000).Should().BeEquivalentTo(expected);

        await env.WriteReceiptAsync(StateHash(afterRestart));
    }

    private static string StateHash(IReadOnlyDictionary<long, string> state)
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true);
        foreach (var item in state.OrderBy(item => item.Key))
        {
            writer.Write(item.Key);
            writer.Write(item.Value);
        }

        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(bytes.ToArray()));
    }

    private sealed class CandidateODataEnvironment : IAsyncDisposable
    {
        private const string PostgresImage = "postgis/postgis:18-3.6";
        private const int CandidateContainerPort = 8080;
        private const string DatabasePassword = "candidate-3872-odata-delta";

        private readonly ImageIdentity _image;
        private readonly ITestOutputHelper _output;
        private readonly string _networkName;
        private readonly string _postgresContainerName;
        private readonly string _candidateContainerName;
        private readonly string _postgresConnectionStringFromHost;
        private readonly int _candidateHostPort;

        private CandidateODataEnvironment(
            ImageIdentity image,
            ITestOutputHelper output,
            string networkName,
            string postgresContainerName,
            string candidateContainerName,
            string postgresConnectionStringFromHost,
            int candidateHostPort)
        {
            _image = image;
            _output = output;
            _networkName = networkName;
            _postgresContainerName = postgresContainerName;
            _candidateContainerName = candidateContainerName;
            _postgresConnectionStringFromHost = postgresConnectionStringFromHost;
            _candidateHostPort = candidateHostPort;
            HttpClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{candidateHostPort.ToString(CultureInfo.InvariantCulture)}") };
        }

        public HttpClient HttpClient { get; }

        public static async Task<CandidateODataEnvironment> StartAsync(ImageIdentity image, ITestOutputHelper output)
        {
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var networkName = $"honua-cod-{suffix}";
            var postgresContainerName = $"honua-cod-pg-{suffix}";
            var candidateContainerName = $"honua-cod-server-{suffix}";

            await Docker.RunCheckedAsync(["network", "create", networkName]);
            try
            {
                await Docker.RunCheckedAsync(
                [
                    "run", "-d", "--name", postgresContainerName, "--network", networkName,
                    "-p", "127.0.0.1:0:5432",
                    "-e", "POSTGRES_USER=honua", "-e", $"POSTGRES_PASSWORD={DatabasePassword}", "-e", "POSTGRES_DB=honua",
                    PostgresImage
                ]);
                await WaitForPgReadyAsync(postgresContainerName);
                var hostPort = await Docker.MappedHostPortAsync(postgresContainerName, 5432);
                var postgresConnectionStringFromHost =
                    $"Host=127.0.0.1;Port={hostPort.ToString(CultureInfo.InvariantCulture)};Database=honua;Username=honua;Password={DatabasePassword}";

                // Boot the candidate first so its own migrations create the schema (including the
                // metadata_v2_snapshots family the compat activation below writes into) exactly as
                // a real deployment would; only then seed and activate the V1-to-V2 projection
                // against the live database, which the running candidate resolves on its very next
                // request with no restart or cache-invalidation race.
                var candidateHostPort = LocalSubstrateDockerFixture.GetFreeTcpPort();
                await Docker.RunCheckedAsync(
                [
                    "run", "-d", "--name", candidateContainerName, "--network", networkName,
                    "-p", $"127.0.0.1:{candidateHostPort.ToString(CultureInfo.InvariantCulture)}:{CandidateContainerPort.ToString(CultureInfo.InvariantCulture)}",
                    "-e", "ASPNETCORE_ENVIRONMENT=Development",
                    "-e", $"ConnectionStrings__DefaultConnection=Host={postgresContainerName};Port=5432;Database=honua;Username=honua;Password={DatabasePassword}",
                    "-e", "HONUA_ADMIN_PASSWORD=Candidate-3872-Odata-Delta!admin",
                    "-e", "Security__ConnectionEncryption__MasterKey=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                    "-e", "AllowedHosts=*",
                    image.Id
                ]);

                var env = new CandidateODataEnvironment(
                    image,
                    output,
                    networkName,
                    postgresContainerName,
                    candidateContainerName,
                    postgresConnectionStringFromHost,
                    candidateHostPort);
                await env.WaitForCandidateReadyAsync();

                await SeedV1CatalogAsync(postgresConnectionStringFromHost);
                await ActivateCompatSnapshotAsync(postgresConnectionStringFromHost);

                return env;
            }
            catch
            {
                await Docker.RunAsync(["rm", "-f", candidateContainerName]);
                await Docker.RunAsync(["rm", "-f", postgresContainerName]);
                await Docker.RunAsync(["network", "rm", networkName]);
                throw;
            }
        }

        public Task<NpgsqlConnection> OpenPostgresConnectionAsync() => OpenConnectionAsync(_postgresConnectionStringFromHost);

        private static async Task<NpgsqlConnection> OpenConnectionAsync(string connectionString)
        {
            var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            return connection;
        }

        public async Task SeedScenarioRowsAsync()
        {
            await using var connection = await OpenPostgresConnectionAsync();
            await using var command = new NpgsqlCommand(
                """
                DELETE FROM honua.features WHERE layer_id = 0 AND objectid >= 73000;
                INSERT INTO honua.features(objectid, layer_id, geometry, attributes, updated_at)
                VALUES
                    (73001, 0, ST_SetSRID(ST_Point(1, 2), 4326), jsonb_build_object('objectid', 73001, 'name', 'first'), '2026-01-01'),
                    (73002, 0, ST_SetSRID(ST_Point(2, 3), 4326), jsonb_build_object('objectid', 73002, 'name', 'second'), '2026-01-01'),
                    (73003, 0, NULL, jsonb_build_object('objectid', 73003, 'name', 'leaving'), '2026-01-01'),
                    (73004, 0, NULL, jsonb_build_object('objectid', 73004, 'name', 'recreate'), '2026-01-01'),
                    (73005, 0, NULL, jsonb_build_object('objectid', 73005, 'name', 'excluded'), '2026-01-01'),
                    (73006, 0, NULL, jsonb_build_object('objectid', 73006, 'name', 'delete'), '2026-01-01');
                """,
                connection);
            await command.ExecuteNonQueryAsync();
        }

        public async Task MutateScenarioRowsAsync()
        {
            await using var connection = await OpenPostgresConnectionAsync();
            // One frozen database timestamp, deliberately older than wall-clock: change
            // generations, not timestamp precision, must drive delivery.
            await using var command = new NpgsqlCommand(
                """
                UPDATE honua.features SET attributes = jsonb_build_object('objectid', 73001, 'name', 'first-updated'), updated_at = '2026-01-02' WHERE objectid = 73001;
                UPDATE honua.features SET attributes = jsonb_build_object('objectid', 73002, 'name', 'second-updated'), geometry = ST_SetSRID(ST_Point(7, 11), 4326), updated_at = '2026-01-02' WHERE objectid = 73002;
                UPDATE honua.features SET attributes = jsonb_build_object('objectid', 73003, 'name', 'excluded'), updated_at = '2026-01-02' WHERE objectid = 73003;
                DELETE FROM honua.features WHERE objectid IN (73004, 73006);
                INSERT INTO honua.features(objectid, layer_id, attributes, updated_at) VALUES (73004, 0, jsonb_build_object('objectid', 73004, 'name', 'recreated'), '2026-01-02');
                UPDATE honua.features SET attributes = jsonb_build_object('objectid', 73005, 'name', 'entered'), updated_at = '2026-01-02' WHERE objectid = 73005;
                """,
                connection);
            await command.ExecuteNonQueryAsync();
        }

        public async Task ApplyPostRestartMutationAsync()
        {
            await using var connection = await OpenPostgresConnectionAsync();
            await using var command = new NpgsqlCommand(
                "UPDATE honua.features SET attributes = jsonb_build_object('objectid', 73001, 'name', 'after-restart'), updated_at = '2026-01-02' " +
                "WHERE layer_id = 0 AND objectid = 73001;",
                connection);
            await command.ExecuteNonQueryAsync();
        }

        public async Task RestartCandidateAsync()
        {
            _output.WriteLine($"Restarting the candidate container '{_candidateContainerName}'.");
            await Docker.RunCheckedAsync(["restart", _candidateContainerName]);
            await WaitForCandidateReadyAsync();
        }

        public async Task WriteReceiptAsync(string finalStateHash)
        {
            var receipt = new Receipt(
                Schema: "honua.candidate-odata-delta-receipt/v1",
                Scenario: "equal-timestamps-deletes-recreate-filter-transitions-restart",
                Candidate: _image,
                FinalStateHash: finalStateHash,
                CompletedAt: DateTimeOffset.UtcNow);
            var json = JsonSerializer.Serialize(receipt, ReceiptJsonOptions);
            _output.WriteLine(json);

            var receiptDirectory = Environment.GetEnvironmentVariable(ReceiptDirectoryEnvVar);
            if (!string.IsNullOrWhiteSpace(receiptDirectory))
            {
                Directory.CreateDirectory(receiptDirectory);
                await File.WriteAllTextAsync(Path.Join(receiptDirectory, "odata-delta.json"), json);
            }
        }

        private async Task WaitForCandidateReadyAsync()
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(3);
            while (DateTimeOffset.UtcNow < deadline)
            {
                try
                {
                    using var response = await HttpClient.GetAsync("/healthz/ready");
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        return;
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    // Candidate still starting.
                }

                await Task.Delay(TimeSpan.FromSeconds(2));
            }

            throw new TimeoutException($"Candidate container '{_candidateContainerName}' never reached /healthz/ready.");
        }

        private static async Task WaitForPgReadyAsync(string containerName)
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(2);
            while (DateTimeOffset.UtcNow < deadline)
            {
                // pg_isready over TCP, not the default local socket: the image's init phase answers
                // on the unix socket before the final server the harness connects to is listening,
                // and the container restarts once after initdb (see CandidateTelemetryGateCertificationTests'
                // StartPostgresAsync in the sibling deploy-gate certification lane, same lesson).
                var (exitCode, _, _) = await Docker.RunAsync(["exec", containerName, "pg_isready", "-h", "127.0.0.1", "-U", "honua", "-d", "honua"]);
                if (exitCode == 0)
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(1));
            }

            throw new TimeoutException($"Postgres container '{containerName}' never became ready.");
        }

        /// <summary>
        /// Seeds the legacy V1 catalog with the same SQL the in-process fixture applies
        /// (<c>tests/seed/odata.yaml</c>), so the candidate-cert layer is byte-identical to the one
        /// <c>ODataDeltaTests.Convergence.cs</c> exercises in-process.
        /// </summary>
        private static async Task SeedV1CatalogAsync(string connectionString)
        {
            var seedPath = ResolveRepoRelativePath(Path.Join("tests", "seed", "odata.yaml"));
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
            var seed = deserializer.Deserialize<SeedFile>(await File.ReadAllTextAsync(seedPath));
            (seed.Sql ?? []).Should().NotBeEmpty("the odata.yaml seed must declare at least one SQL statement");

            await using var connection = await OpenConnectionAsync(connectionString);
            foreach (var statement in seed.Sql!)
            {
                await using var command = new NpgsqlCommand(statement, connection);
                await command.ExecuteNonQueryAsync();
            }
        }

        /// <summary>
        /// Activates the same V1-to-V2 compat projection production code applies on a real publish
        /// (<c>PostgresMetadataV2LegacyCatalogProjector.BuildFromLegacyCatalogAsync</c> /
        /// <c>CloudDemoServiceSeeder.ProjectLegacyCatalogIntoGraphAsync</c>). This harness runs
        /// outside the candidate's process, so it executes the equivalent SQL function the shared
        /// test fixture already ships and already uses for the same purpose
        /// (<c>tests/seed/base-schema.sql</c>'s <c>honua.seed_metadata_v2_compat_snapshot()</c>)
        /// against the target database, before the candidate's first boot.
        /// </summary>
        private static async Task ActivateCompatSnapshotAsync(string connectionString)
        {
            var baseSchemaPath = ResolveRepoRelativePath(Path.Join("tests", "seed", "base-schema.sql"));
            var baseSchema = await File.ReadAllTextAsync(baseSchemaPath);
            const string marker = "CREATE OR REPLACE FUNCTION honua.seed_metadata_v2_compat_snapshot()";
            var start = baseSchema.IndexOf(marker, StringComparison.Ordinal);
            start.Should().BeGreaterThanOrEqualTo(0, $"'{marker}' must exist in tests/seed/base-schema.sql");
            var end = baseSchema.IndexOf("\n$$;", start, StringComparison.Ordinal);
            end.Should().BeGreaterThan(start, "the function body must terminate with '$$;'");
            var functionSql = baseSchema[start..(end + "\n$$;".Length)];

            await using var connection = await OpenConnectionAsync(connectionString);
            await using (var create = new NpgsqlCommand(functionSql, connection))
            {
                await create.ExecuteNonQueryAsync();
            }

            await using var call = new NpgsqlCommand("SELECT honua.seed_metadata_v2_compat_snapshot();", connection);
            await call.ExecuteNonQueryAsync();
        }

        private static string ResolveRepoRelativePath(string relativePath)
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Join(directory.FullName, "Honua.sln")))
            {
                directory = directory.Parent;
            }

            directory.Should().NotBeNull("the test host must run inside the honua-server repository checkout");
            var resolved = Path.Join(directory!.FullName, relativePath);
            File.Exists(resolved).Should().BeTrue($"'{resolved}' must exist");
            return resolved;
        }

        public async ValueTask DisposeAsync()
        {
            HttpClient.Dispose();
            await Docker.RunAsync(["rm", "-f", _candidateContainerName]);
            await Docker.RunAsync(["rm", "-f", _postgresContainerName]);
            await Docker.RunAsync(["network", "rm", _networkName]);
        }
    }

    private sealed record SeedFile
    {
        public List<string>? Sql { get; init; }
    }

    private sealed record ImageIdentity(string Reference, string Id, IReadOnlyList<string> RepoDigests);

    private sealed record Receipt(
        string Schema,
        string Scenario,
        ImageIdentity Candidate,
        string FinalStateHash,
        DateTimeOffset CompletedAt);

    private static readonly JsonSerializerOptions ReceiptJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static class Docker
    {
        public static async Task<bool> IsAvailableAsync()
        {
            try
            {
                var (exitCode, _, _) = await RunAsync(["version", "--format", "{{.Server.Version}}"]);
                return exitCode == 0;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return false;
            }
        }

        public static async Task<ImageIdentity> ResolveImageAsync(string reference)
        {
            var (exitCode, stdout, _) = await RunAsync(["image", "inspect", "-f", "{{.Id}}|{{join .RepoDigests \",\"}}", reference]);
            if (exitCode != 0)
            {
                await RunCheckedAsync(["pull", "-q", reference]);
                (exitCode, stdout, _) = await RunAsync(["image", "inspect", "-f", "{{.Id}}|{{join .RepoDigests \",\"}}", reference]);
            }

            exitCode.Should().Be(0, $"image '{reference}' must be resolvable to an immutable id");
            var parts = stdout.Trim().Split('|');
            return new ImageIdentity(
                reference,
                parts[0],
                parts.Length > 1 ? parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries) : []);
        }

        public static async Task<int> MappedHostPortAsync(string containerName, int containerPort)
        {
            var (exitCode, stdout, stderr) = await RunAsync(["port", containerName, containerPort.ToString(CultureInfo.InvariantCulture)]);
            exitCode.Should().Be(0, $"container '{containerName}' must publish port {containerPort.ToString(CultureInfo.InvariantCulture)}: {stderr}");
            var mapping = stdout.Trim().Split('\n')[0];
            var port = mapping[(mapping.LastIndexOf(':') + 1)..];
            return int.Parse(port, CultureInfo.InvariantCulture);
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

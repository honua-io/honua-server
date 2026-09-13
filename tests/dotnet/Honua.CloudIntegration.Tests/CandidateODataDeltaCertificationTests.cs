// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit.Seeding;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Honua.CloudIntegration.Tests;

/// <summary>
/// Exact-candidate OData lossless-delta certification (honua-server#3872). Boots the real,
/// digest-addressed candidate server image (<c>HONUA_CANDIDATE_IMAGE</c>) against a real
/// PostGIS and Redis, over the production container path, then drives the equal-timestamp /
/// delete / recreate / filter-transition delta scenario, a full container restart, and a set
/// of externally reachable invalid-continuation cases entirely over real HTTP against that
/// container — never in-process.
/// </summary>
/// <remarks>
/// <para>
/// Requires Docker and <c>HONUA_CANDIDATE_IMAGE</c>; the test <c>[SkippableFact]</c>-skips
/// when either is unavailable. Writes an immutable JSON receipt (image identity, source
/// revision, per-page state hashes, and the pass/fail outcome) to
/// <c>HONUA_CANDIDATE_RECEIPT_DIR</c> when set, and to the test output otherwise.
/// </para>
/// <para>
/// The candidate image ships with no activated Metadata v2 snapshot beyond the built-in
/// GPServer bootstrap entry (honua-server#2349); the read path never re-derives one from the
/// legacy V1 catalog once any snapshot has been activated (honua-server#1412). This harness
/// seeds the V1 catalog (<c>tests/seed/odata.yaml</c>, byte-identical to the in-process
/// fixture) and then merges the same V1-to-V2 compat projection the server itself would
/// synthesize (<c>MergeLegacyCatalogIntoActiveGraphAsync</c>) into the already-activated
/// graph, so the real, unmodified read path resolves the seeded layer exactly as it would
/// after a real publish.
/// </para>
/// </remarks>
[Trait(CloudIntegrationTraits.Category, CloudIntegrationTraits.CandidateCertification)]
public sealed class CandidateODataDeltaCertificationTests
{
    private const string CandidateImageEnvVar = "HONUA_CANDIDATE_IMAGE";
    private const string ReceiptDirectoryEnvVar = "HONUA_CANDIDATE_RECEIPT_DIR";

    private readonly ITestOutputHelper _output;

    public CandidateODataDeltaCertificationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SkippableFact]
    public async Task ExactCandidate_EqualTimestampsDeletesRecreateFilterTransitionsRestartAndInvalidContinuations_Converge()
    {
        var imageReference = Environment.GetEnvironmentVariable(CandidateImageEnvVar);
        Skip.If(string.IsNullOrWhiteSpace(imageReference), $"Set {CandidateImageEnvVar} to run the exact-candidate OData delta lane.");
        Skip.IfNot(await Docker.IsAvailableAsync(), "Docker is not available for the exact-candidate OData delta lane.");

        await using var env = await CandidateODataEnvironment.StartAsync(imageReference!, _output);

        var state = new Dictionary<long, string>();

        async Task<Dictionary<long, string>> AuthoritativeAsync()
        {
            var rows = new Dictionary<long, string>();
            await using var command = env.DataSource.CreateCommand(
                "SELECT objectid, attributes->>'name' FROM public.features WHERE layer_id = 0 AND attributes->>'name' <> 'excluded' ORDER BY objectid;");
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
                (++pages).Should().BeLessThan(20, "paging must terminate without unbounded duplicates");
                using var request = new HttpRequestMessage(HttpMethod.Get, env.ToLocalPathAndQuery(link));
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
                        else
                        {
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

        await env.SeedEqualTimestampFixtureAsync();

        var delta = await FollowAsync("/odata/Features(0)?$filter=name%20ne%20'excluded'&$top=1", true);
        state.Should().BeEquivalentTo(new Dictionary<long, string>
        {
            [73001] = "first",
            [73002] = "second",
            [73003] = "leaving",
            [73004] = "recreate",
            [73006] = "delete"
        });

        await env.MutateEqualTimestampFixtureAsync();

        var terminal = await FollowAsync(delta, false);
        var expected = new Dictionary<long, string>
        {
            [73001] = "first-updated",
            [73002] = "second-updated",
            [73004] = "recreated",
            [73005] = "entered"
        };
        state.Should().BeEquivalentTo(expected, "the independently specified mutation outcome must replace the baseline");
        StateHash(state).Should().Be(StateHash(expected));
        StateHash(state).Should().Be(StateHash(await AuthoritativeAsync()), "the subscriber converges to an independent direct SQL query against the real candidate database");
        _ = await FollowAsync(terminal, false);
        state.Should().BeEquivalentTo(expected, "terminal polling is idempotent");

        await env.RestartCandidateContainerAsync();
        _ = await FollowAsync(terminal, false);
        state.Should().BeEquivalentTo(expected, "the same durable terminal token survives a complete real-container restart of the exact candidate");

        await env.MutateAfterRestartAsync();
        expected[73001] = "after-restart";
        var restartedTerminal = await FollowAsync(terminal, false);
        state.Should().BeEquivalentTo(expected, "a post-restart update at the same timestamp must converge without rebasing");
        StateHash(state).Should().Be(StateHash(expected));
        StateHash(state).Should().Be(StateHash(await AuthoritativeAsync()));
        _ = await FollowAsync(restartedTerminal, false);
        state.Should().BeEquivalentTo(expected);

        var invalidContinuationOutcomes = new List<(string Scenario, HttpStatusCode Status, string Code)>();
        foreach (var (scenario, token, expectedStatus, expectedCode) in InvalidContinuations())
        {
            using var response = await env.HttpClient.GetAsync($"/odata/Features(0)?$deltatoken={token}");
            response.StatusCode.Should().Be(expectedStatus, "scenario {0}", scenario);
            using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            error.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be(expectedCode, "scenario {0}", scenario);
            error.RootElement.TryGetProperty("value", out _).Should().BeFalse("scenario {0} must not leak values", scenario);
            error.RootElement.TryGetProperty("@odata.deltaLink", out _).Should().BeFalse("scenario {0} must not silently rebaseline", scenario);
            invalidContinuationOutcomes.Add((scenario, response.StatusCode, expectedCode));
        }

        await env.WriteReceiptAsync(finalState: expected, invalidContinuationOutcomes);
    }

    private static IEnumerable<(string Scenario, string Token, HttpStatusCode Status, string Code)> InvalidContinuations()
    {
        // Reachable purely over external HTTP with no in-process store access: a token
        // referencing no stored snapshot, a negative page ordinal, and an empty token. The
        // expired/future/scope scenarios in the in-process suite
        // (ODataDeltaTests.Recovery.cs) manipulate IQuerySnapshotStore directly and have no
        // externally reachable equivalent against a real deployed candidate.
        yield return ("missing", $"v2.{Guid.NewGuid():N}.0.t", HttpStatusCode.Gone, "DeltaTokenExpired");
        yield return ("malformed", $"v2.{Guid.NewGuid():N}.-1.p", HttpStatusCode.BadRequest, "InvalidQueryOption");
        yield return ("empty", string.Empty, HttpStatusCode.BadRequest, "InvalidQueryOption");
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
        private const string RedisImage = "redis:7.2-alpine";
        private const string AdminPassword = "Candidate-3872!OData";
        private const string MasterKey = "candidate-3872-odata-master-key-0123456789ab";
        private const string EnvironmentName = "Production";

        private readonly ITestOutputHelper _output;
        private readonly string _network;
        private readonly string _pgName;
        private readonly string _redisName;
        private readonly string _serverName;
        private readonly int _port;
        private readonly DateTimeOffset _startedAt;

        private CandidateODataEnvironment(
            ITestOutputHelper output,
            string network,
            string pgName,
            string redisName,
            string serverName,
            int port,
            NpgsqlDataSource dataSource,
            HttpClient httpClient,
            Docker.ImageIdentity image)
        {
            _output = output;
            _network = network;
            _pgName = pgName;
            _redisName = redisName;
            _serverName = serverName;
            _port = port;
            DataSource = dataSource;
            HttpClient = httpClient;
            Image = image;
            _startedAt = DateTimeOffset.UtcNow;
        }

        public NpgsqlDataSource DataSource { get; }

        public HttpClient HttpClient { get; }

        public Docker.ImageIdentity Image { get; }

        public string ToLocalPathAndQuery(string link)
            // A root-relative URL is also an absolute file URI on Unix; preserve its query
            // string instead of converting it to a file path.
            => link.StartsWith('/') ? link : new Uri(link).PathAndQuery;

        public static async Task<CandidateODataEnvironment> StartAsync(string imageReference, ITestOutputHelper output)
        {
            var image = await Docker.ResolveImageAsync(imageReference);
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var network = $"honua-cand-odata-{suffix}";
            var pgName = $"{network}-pg";
            var redisName = $"{network}-redis";
            var serverName = $"{network}-srv";
            var port = LocalSubstrateDockerFixture.GetFreeTcpPort();
            var pgPort = LocalSubstrateDockerFixture.GetFreeTcpPort();

            await Docker.RunCheckedAsync(["network", "create", network]);
            try
            {
                // Docker Desktop's bridge-network container IPs are only reachable from
                // inside its VM, not from the WSL host running this test process, so the
                // seeding/merge connection below must go through a published host port
                // rather than the container's bridge address (honua-server#4617 boot gotcha).
                await Docker.RunCheckedAsync(
                    ["run", "-d", "--name", pgName, "--network", network,
                        "-p", $"127.0.0.1:{pgPort.ToString(CultureInfo.InvariantCulture)}:5432",
                        "-e", "POSTGRES_USER=honua", "-e", "POSTGRES_PASSWORD=candidate-3872", "-e", "POSTGRES_DB=honua",
                        PostgresImage]);
                await WaitForPostgresReadyAsync(pgName);

                await Docker.RunCheckedAsync(["run", "-d", "--name", redisName, "--network", network, RedisImage]);

                await Docker.RunCheckedAsync(
                [
                    "run", "-d", "--name", serverName, "--network", network,
                    "-p", $"127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}:8080",
                    "-e", $"ConnectionStrings__DefaultConnection=Host={pgName};Database=honua;Username=honua;Password=candidate-3872",
                    "-e", $"ConnectionStrings__Redis={redisName}:6379",
                    "-e", $"HONUA_ADMIN_PASSWORD={AdminPassword}",
                    "-e", "HostValidation__AllowedHosts__0=127.0.0.1",
                    "-e", "HostValidation__AllowedHosts__1=localhost",
                    "-e", $"Security__ConnectionEncryption__MasterKey={MasterKey}",
                    "-e", $"Security__ConnectionEncryption__Salt={Convert.ToBase64String(Encoding.UTF8.GetBytes($"candidate-3872-odata-salt-{suffix}"))}",
                    "-e", "Licensing__Mode=Disabled",
                    imageReference
                ]);

                var httpClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}") };
                (await WaitForStatusAsync(httpClient, "/healthz/ready", TimeSpan.FromMinutes(3)))
                    .Should().BeTrue("the exact-candidate server must become ready before the delta scenario runs");

                var dataSource = NpgsqlDataSource.Create(
                    $"Host=127.0.0.1;Port={pgPort.ToString(CultureInfo.InvariantCulture)};Database=honua;Username=honua;Password=candidate-3872");

                await SeedRunner.ApplyAsync(dataSource, ResolveSeedPath(), "public", null);
                await MergeLegacyCatalogIntoActiveGraphAsync(dataSource, EnvironmentName);
                await RestartServerAndWaitReadyAsync(serverName, httpClient);

                return new CandidateODataEnvironment(output, network, pgName, redisName, serverName, port, dataSource, httpClient, image);
            }
            catch
            {
                await BestEffortCleanupAsync(network, pgName, redisName, serverName);
                throw;
            }
        }

        public async Task SeedEqualTimestampFixtureAsync()
        {
            await using var command = DataSource.CreateCommand("""
                DELETE FROM public.features WHERE layer_id = 0;
                INSERT INTO public.features(objectid, layer_id, geometry, attributes, updated_at)
                VALUES
                    (73001, 0, ST_SetSRID(ST_Point(1, 2), 4326), '{"name":"first"}', '2026-01-01'),
                    (73002, 0, ST_SetSRID(ST_Point(2, 3), 4326), '{"name":"second"}', '2026-01-01'),
                    (73003, 0, NULL, '{"name":"leaving"}', '2026-01-01'),
                    (73004, 0, NULL, '{"name":"recreate"}', '2026-01-01'),
                    (73005, 0, NULL, '{"name":"excluded"}', '2026-01-01'),
                    (73006, 0, NULL, '{"name":"delete"}', '2026-01-01');
                """);
            await command.ExecuteNonQueryAsync();
        }

        public async Task MutateEqualTimestampFixtureAsync()
        {
            await using var command = DataSource.CreateCommand("""
                UPDATE public.features SET attributes = '{"name":"first-updated"}', updated_at = '2026-01-02' WHERE objectid = 73001;
                UPDATE public.features SET attributes = '{"name":"second-updated"}', geometry = ST_SetSRID(ST_Point(7, 11), 4326), updated_at = '2026-01-02' WHERE objectid = 73002;
                UPDATE public.features SET attributes = '{"name":"excluded"}', updated_at = '2026-01-02' WHERE objectid = 73003;
                DELETE FROM public.features WHERE objectid IN (73004, 73006);
                INSERT INTO public.features(objectid, layer_id, attributes, updated_at) VALUES (73004, 0, '{"name":"recreated"}', '2026-01-02');
                UPDATE public.features SET attributes = '{"name":"entered"}', updated_at = '2026-01-02' WHERE objectid = 73005;
                """);
            await command.ExecuteNonQueryAsync();
        }

        public async Task MutateAfterRestartAsync()
        {
            await using var command = DataSource.CreateCommand("""
                UPDATE public.features SET attributes = '{"name":"after-restart"}', updated_at = '2026-01-02'
                WHERE layer_id = 0 AND objectid = 73001;
                """);
            await command.ExecuteNonQueryAsync();
        }

        public async Task RestartCandidateContainerAsync()
        {
            await Docker.RunCheckedAsync(["restart", _serverName]);
            (await WaitForStatusAsync(HttpClient, "/healthz/ready", TimeSpan.FromMinutes(3)))
                .Should().BeTrue("the exact-candidate server must come back ready after a full container restart");
        }

        public async Task WriteReceiptAsync(
            IReadOnlyDictionary<long, string> finalState,
            IReadOnlyList<(string Scenario, HttpStatusCode Status, string Code)> invalidContinuationOutcomes)
        {
            var receipt = new
            {
                schema = "honua.candidate-odata-delta-certification.v1",
                issue = 3872,
                candidateImage = new { reference = Image.Reference, id = Image.Id, repoDigests = Image.RepoDigests, sourceRevision = Image.SourceRevision },
                startedAt = _startedAt,
                completedAt = DateTimeOffset.UtcNow,
                finalStateHash = StateHash(finalState),
                finalState,
                invalidContinuationOutcomes = invalidContinuationOutcomes
                    .Select(o => new { o.Scenario, status = (int)o.Status, o.Code }),
            };
            var json = JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true });

            var directory = Environment.GetEnvironmentVariable(ReceiptDirectoryEnvVar);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
                await File.WriteAllTextAsync(Path.Join(directory, $"odata-delta-{Image.SourceRevision}.json"), json);
            }
            else
            {
                _output.WriteLine(json);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await DataSource.DisposeAsync();
            HttpClient.Dispose();
            await BestEffortCleanupAsync(_network, _pgName, _redisName, _serverName);
        }

        private static async Task BestEffortCleanupAsync(string network, string pgName, string redisName, string serverName)
        {
            await Docker.RunAsync(["rm", "-f", pgName, redisName, serverName]);
            await Docker.RunAsync(["network", "rm", network]);
        }

        private static async Task RestartServerAndWaitReadyAsync(string serverName, HttpClient httpClient)
        {
            await Docker.RunCheckedAsync(["restart", serverName]);
            (await WaitForStatusAsync(httpClient, "/healthz/ready", TimeSpan.FromMinutes(3)))
                .Should().BeTrue("the exact-candidate server must be ready after activating the merged catalog");
        }

        private static async Task WaitForPostgresReadyAsync(string name)
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(2);
            while (DateTimeOffset.UtcNow < deadline)
            {
                var (exitCode, _, _) = await Docker.RunAsync(["exec", name, "pg_isready", "-h", "127.0.0.1", "-U", "honua", "-d", "honua"]);
                if (exitCode == 0)
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(1));
            }

            throw new InvalidOperationException($"PostGIS container '{name}' did not become ready.");
        }

        private static async Task<bool> WaitForStatusAsync(HttpClient client, string path, TimeSpan timeout)
        {
            var deadline = DateTimeOffset.UtcNow.Add(timeout);
            while (DateTimeOffset.UtcNow < deadline)
            {
                try
                {
                    using var response = await client.GetAsync(path);
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        return true;
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    // Server still starting.
                }

                await Task.Delay(TimeSpan.FromSeconds(1));
            }

            return false;
        }

        private static string ResolveSeedPath()
        {
            var directory = AppContext.BaseDirectory;
            for (var i = 0; i < 8 && directory is not null; i++)
            {
                var candidate = Path.Join(directory, "tests", "seed", "odata.yaml");
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = Path.GetDirectoryName(directory);
            }

            throw new FileNotFoundException("Could not locate tests/seed/odata.yaml relative to the test binary.");
        }

        /// <summary>
        /// Merges the same V1-catalog-to-V2-graph projection
        /// <c>MetadataV2CompatSnapshotSql.BuildDocumentFromV1Catalog</c> synthesizes into the
        /// exact candidate's already-activated Metadata v2 snapshot (the built-in GPServer
        /// bootstrap entry, honua-server#2349), so the seeded layer is resolvable through the
        /// real, unmodified read path (honua-server#1412: the compat synthesis only fires when
        /// no snapshot has ever been activated). This mirrors — at the same read-time SQL the
        /// server ships — the net effect of a real publish, without inventing a new schema.
        /// </summary>
        private static async Task MergeLegacyCatalogIntoActiveGraphAsync(NpgsqlDataSource dataSource, string environment)
        {
            const string mergeSql = """
                WITH
                current_doc AS (
                    SELECT c.revision AS rev, s.document AS doc
                    FROM honua.metadata_v2_current c
                    JOIN honua.metadata_v2_snapshots s ON s.environment = c.environment AND s.revision = c.revision
                    WHERE c.environment = @environment
                ),
                compat_cte AS (
                    SELECT __COMPAT_SELECT__ AS doc
                ),
                merged AS (
                    SELECT
                        (SELECT rev FROM current_doc) + 1 AS new_revision,
                        jsonb_set(
                            jsonb_set(
                                jsonb_set(
                                    jsonb_set(
                                        (SELECT doc FROM current_doc),
                                        '{services}',
                                        (((SELECT doc FROM current_doc) -> 'services') || ((SELECT doc FROM compat_cte) -> 'services'))
                                    ),
                                    '{resources}',
                                    (((SELECT doc FROM current_doc) -> 'resources') || ((SELECT doc FROM compat_cte) -> 'resources'))
                                ),
                                '{storageBindings}',
                                (((SELECT doc FROM current_doc) -> 'storageBindings') || ((SELECT doc FROM compat_cte) -> 'storageBindings'))
                            ),
                            '{publications}',
                            (((SELECT doc FROM current_doc) -> 'publications') || ((SELECT doc FROM compat_cte) -> 'publications'))
                        ) AS doc
                )
                INSERT INTO honua.metadata_v2_snapshots (environment, revision, schema_version, api_version, document, etag, generated_at)
                SELECT @environment, new_revision,
                       (SELECT doc FROM current_doc) ->> 'schemaVersion',
                       (SELECT doc FROM current_doc) ->> 'apiVersion',
                       jsonb_set(doc, '{revision}', to_jsonb(new_revision)),
                       md5(doc::text || new_revision::text),
                       now()
                FROM merged;
                """;

            var compatSelect = "(" + Honua.Db.Postgres.Features.Metadata.MetadataV2CompatSnapshotSql.BuildDocumentFromV1Catalog
                .Replace(Honua.Db.Postgres.Features.Metadata.MetadataV2CompatSnapshotSql.CatalogSchemaPlaceholder, "honua", StringComparison.Ordinal) + ")";
            var sql = mergeSql.Replace("__COMPAT_SELECT__", compatSelect, StringComparison.Ordinal);

            await using var connection = await dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using (var command = new NpgsqlCommand(sql, connection, transaction))
            {
                command.Parameters.AddWithValue("@environment", environment);
                await command.ExecuteNonQueryAsync();
            }

            await using (var updateCurrent = new NpgsqlCommand(
                """
                UPDATE honua.metadata_v2_current c
                SET revision = latest.revision, etag = latest.etag, activated_at = now()
                FROM (
                    SELECT revision, etag FROM honua.metadata_v2_snapshots
                    WHERE environment = @environment
                    ORDER BY revision DESC LIMIT 1
                ) latest
                WHERE c.environment = @environment;
                """, connection, transaction))
            {
                updateCurrent.Parameters.AddWithValue("@environment", environment);
                await updateCurrent.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }
    }

    private static class Docker
    {
        public static async Task<bool> IsAvailableAsync()
        {
            var (exitCode, _, _) = await RunAsync(["version", "--format", "{{.Server.Version}}"]);
            return exitCode == 0;
        }

        public static async Task<ImageIdentity> ResolveImageAsync(string reference)
        {
            var (exitCode, stdout, _) = await RunAsync(
                ["image", "inspect", "-f", "{{.Id}}|{{join .RepoDigests \",\"}}|{{index .Config.Labels \"org.opencontainers.image.revision\"}}", reference]);
            if (exitCode != 0)
            {
                await RunCheckedAsync(["pull", "-q", reference]);
                (exitCode, stdout, _) = await RunAsync(
                    ["image", "inspect", "-f", "{{.Id}}|{{join .RepoDigests \",\"}}|{{index .Config.Labels \"org.opencontainers.image.revision\"}}", reference]);
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
            var startInfo = new System.Diagnostics.ProcessStartInfo
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

            using var process = new System.Diagnostics.Process { StartInfo = startInfo };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) { stdout.AppendLine(e.Data); } };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { stderr.AppendLine(e.Data); } };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();
            return (process.ExitCode, stdout.ToString(), stderr.ToString());
        }

        public sealed record ImageIdentity(string Reference, string Id, IReadOnlyList<string> RepoDigests, string SourceRevision);
    }
}

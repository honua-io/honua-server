// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using FluentAssertions.Execution;
using Xunit;
using Xunit.Abstractions;

namespace Honua.CloudIntegration.Tests;

/// <summary>
/// Exact-candidate certification of Redis-managed API keys under Native AOT (honua-server#4571). Boots a real
/// published Honua server image (<c>HONUA_CANDIDATE_IMAGE</c>) as <c>Production</c> against a real PostGIS and a
/// real Redis key registry, then replays the managed-key lifecycle over HTTP: create, list and get, scoped
/// access under the shared admin policy, wrong keys, concurrent validation, rotation and revocation under
/// sustained use, rotation racing revocation, expiry, and a process restart that reads every record back.
/// </summary>
/// <remarks>
/// <para>
/// The in-process test host enables reflection-based JSON serialization, so it cannot reproduce the defect;
/// only the published native image runs with reflection disabled. The lane asserts the image declares
/// <c>honua.runtime.compilation=native-aot</c> and that the server log never reports the reflection-disabled
/// exception. It writes a JSON receipt (image id, repo digests, source revision, per-step statuses) to
/// <c>HONUA_CANDIDATE_RECEIPT_DIR</c> when set, and to the test output otherwise, including on failure.
/// </para>
/// <para>
/// These checks prove the server's HTTP contract only. They do not award native QGIS or ArcGIS Pro
/// authentication passes, which remain separate desktop results.
/// </para>
/// </remarks>
[Trait(CloudIntegrationTraits.Category, CloudIntegrationTraits.CandidateCertification)]
public sealed class CandidateManagedApiKeyAotCertificationTests(ITestOutputHelper output)
{
    private const string CandidateImageEnvVar = "HONUA_CANDIDATE_IMAGE";
    private const string ReceiptDirectoryEnvVar = "HONUA_CANDIDATE_RECEIPT_DIR";
    private const string KeysPath = "/api/v1/admin/api-keys";
    private const string ScopedGrant = "read:arcgis_compat_scoped";
    private const string ReflectionDisabledMessage = "Reflection-based serialization has been disabled";
    private const int RaceRounds = 10;

    [SkippableFact]
    public async Task ManagedKeyLifecycle_OnProductionNativeAotCandidateWithRedis_HoldsScopeExpiryRotationRevocationAndRaces()
    {
        var reference = Environment.GetEnvironmentVariable(CandidateImageEnvVar);
        Skip.If(string.IsNullOrWhiteSpace(reference), $"Set {CandidateImageEnvVar} to certify managed API keys on a candidate image.");
        Skip.IfNot((await Docker.RunAsync(["version", "--format", "{{.Server.Version}}"])).ExitCode == 0, "Docker is not available.");

        var image = await Docker.ResolveImageAsync(reference!);
        await using var server = await CandidateServer.StartAsync(image, output);
        var replay = new Replay(server.BaseUrl, output);
        var startedAt = DateTimeOffset.UtcNow;
        var reflectionFailures = -1;
        try
        {
            image.Compilation.Should().Be("native-aot", "only a native image runs with reflection-based serialization disabled");

            // Collect every failed expectation rather than stopping at the first, so the receipt
            // records the outcome of each later lifecycle step on a candidate with a defect.
            using var scope = new AssertionScope();
            await ReplayLifecycleAsync(replay, server);
        }
        finally
        {
            reflectionFailures = await server.CountLogOccurrencesAsync(ReflectionDisabledMessage);
            await replay.WriteReceiptAsync(image, startedAt, reflectionFailures, Environment.GetEnvironmentVariable(ReceiptDirectoryEnvVar));
            replay.Dispose();
        }

        reflectionFailures.Should().Be(0, "no managed-key path may reach reflection-based serialization");
    }

    private static async Task ReplayLifecycleAsync(Replay replay, CandidateServer server)
    {
        var bootstrap = CandidateServer.BootstrapPassword;
        await replay.ExpectAsync("bootstrap-lists-keys", HttpMethod.Get, KeysPath, bootstrap, HttpStatusCode.OK);

        var longExpiry = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds());
        var scoped = await replay.CreateKeyAsync("create-scoped", "aot-scoped-compat", [ScopedGrant], longExpiry);
        scoped.Permissions.Should().Equal(ScopedGrant);
        scoped.ExpiresAt.Should().Be(longExpiry);
        var reader = await replay.CreateKeyAsync("create-admin-read", "aot-admin-read", ["admin:read"], longExpiry);
        var operatorKey = await replay.CreateKeyAsync("create-admin-full", "aot-admin-full", ["admin:*"], longExpiry);
        var survivor = await replay.CreateKeyAsync("create-admin-survivor", "aot-admin-survivor", ["admin:*"], longExpiry);
        var shortExpiry = DateTimeOffset.UtcNow.AddSeconds(20);
        var shortLived = await replay.CreateKeyAsync("create-short-lived", "aot-short-lived", ["admin:*"], shortExpiry);

        // List and get read the generated representation back; plaintext secrets are never listed.
        var listed = await replay.ListAsync("list-after-create");
        listed.Records.Keys.Should().Contain([scoped.Id, reader.Id, operatorKey.Id, survivor.Id, shortLived.Id]);
        listed.Body.Should().NotContain(operatorKey.Secret).And.NotContain(scoped.Secret);
        var scopedPermissions = await replay.EffectivePermissionsAsync("get-scoped", scoped.Id);
        scopedPermissions.Status.Should().Be("active");
        scopedPermissions.CanAuthenticate.Should().BeTrue();
        scopedPermissions.Permissions.Should().Equal(ScopedGrant);

        // Wrong keys: well-formed but never issued, and an issued key missing its last character.
        var unissued = "hnua_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        await replay.ExpectAsync("wrong-key-denied", HttpMethod.Get, KeysPath, unissued, HttpStatusCode.Unauthorized);
        await replay.ExpectAsync("truncated-key-denied", HttpMethod.Get, KeysPath, operatorKey.Secret[..^1], HttpStatusCode.Unauthorized);

        // Shared admin policy: a non-admin scope authenticates but is refused; admin:read reads but cannot mutate.
        await replay.ExpectAsync("scoped-key-refused-admin", HttpMethod.Get, KeysPath, scoped.Secret, HttpStatusCode.Forbidden);
        (await replay.ListAsync("list-after-scoped-use")).Records[scoped.Id].LastUsedAt
            .Should().NotBeNull("the scoped key authenticated (403, not 401) and its usage was written back by compare-and-set");
        await replay.ExpectAsync("admin-read-lists", HttpMethod.Get, KeysPath, reader.Secret, HttpStatusCode.OK);
        await replay.ExpectAsync("admin-read-cannot-create", HttpMethod.Post, KeysPath, reader.Secret, HttpStatusCode.Forbidden, CreateBody("aot-escalation", ["admin:*"], longExpiry));
        await replay.ExpectAsync("admin-read-cannot-rotate", HttpMethod.Post, $"{KeysPath}/{operatorKey.Id}/rotate", reader.Secret, HttpStatusCode.Forbidden);
        await replay.ExpectAsync("admin-read-cannot-revoke", HttpMethod.Post, $"{KeysPath}/{operatorKey.Id}/revoke", reader.Secret, HttpStatusCode.Forbidden);

        // Concurrent validation of one key: every request authenticates despite racing usage writes.
        var burst = await replay.BurstAsync("concurrent-validation", operatorKey.Secret, 32);
        burst.Keys.Should().Equal([200], "losing a LastUsedAt compare-and-set to a sibling request is benign");

        // Rotation while the old secret is in constant use.
        ManagedKey rotated;
        using (var traffic = replay.StartTraffic(operatorKey.Secret))
        {
            rotated = await replay.RotateAsync("rotate-under-load", operatorKey);
            (await replay.StopTrafficAsync("rotate-under-load-traffic", traffic)).Keys.Should().BeSubsetOf([200, 401]);
        }

        await replay.ExpectAsync("old-secret-denied-after-rotate", HttpMethod.Get, KeysPath, operatorKey.Secret, HttpStatusCode.Unauthorized);
        await replay.ExpectAsync("new-secret-accepted-after-rotate", HttpMethod.Get, KeysPath, rotated.Secret, HttpStatusCode.OK);

        // Revocation while the live secret is in constant use must win over its usage writes.
        using (var traffic = replay.StartTraffic(rotated.Secret))
        {
            await replay.ExpectAsync("revoke-under-load", HttpMethod.Post, $"{KeysPath}/{operatorKey.Id}/revoke", bootstrap, HttpStatusCode.OK);
            (await replay.StopTrafficAsync("revoke-under-load-traffic", traffic)).Keys.Should().BeSubsetOf([200, 401]);
        }

        await replay.ExpectAsync("revoked-secret-denied", HttpMethod.Get, KeysPath, rotated.Secret, HttpStatusCode.Unauthorized);
        (await replay.EffectivePermissionsAsync("get-revoked", operatorKey.Id)).Status.Should().Be("revoked");

        // Rotation racing revocation: once the revoke is acknowledged, no secret for the record authenticates.
        for (var round = 0; round < RaceRounds; round++)
        {
            var target = await replay.CreateKeyAsync($"race-{round}-create", $"aot-race-{round}", ["admin:*"], longExpiry);
            var rotate = replay.SendAsync($"race-{round}-rotate", HttpMethod.Post, $"{KeysPath}/{target.Id}/rotate", bootstrap);
            var revoke = replay.SendAsync($"race-{round}-revoke", HttpMethod.Post, $"{KeysPath}/{target.Id}/revoke", bootstrap);
            await Task.WhenAll(rotate, revoke);
            (await revoke).Status.Should().Be(HttpStatusCode.OK);
            (await rotate).Status.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
            await replay.ExpectAsync($"race-{round}-original-denied", HttpMethod.Get, KeysPath, target.Secret, HttpStatusCode.Unauthorized);
            if ((await rotate).Status == HttpStatusCode.OK)
            {
                var racedSecret = ParseCreated((await rotate).Body).Secret;
                await replay.ExpectAsync($"race-{round}-rotated-denied", HttpMethod.Get, KeysPath, racedSecret, HttpStatusCode.Unauthorized);
            }

            (await replay.EffectivePermissionsAsync($"race-{round}-status", target.Id)).Status.Should().Be("revoked");
        }

        // Expiry: the credential stops authenticating, its metadata is retained, and it cannot be rotated back.
        var expiryWait = shortExpiry.AddSeconds(3) - DateTimeOffset.UtcNow;
        if (expiryWait > TimeSpan.Zero)
        {
            await Task.Delay(expiryWait);
        }

        await replay.ExpectAsync("expired-secret-denied", HttpMethod.Get, KeysPath, shortLived.Secret, HttpStatusCode.Unauthorized);
        var expired = await replay.EffectivePermissionsAsync("get-expired", shortLived.Id);
        expired.Status.Should().Be("expired");
        expired.CanAuthenticate.Should().BeFalse();
        await replay.ExpectAsync("rotate-expired-refused", HttpMethod.Post, $"{KeysPath}/{shortLived.Id}/rotate", bootstrap, HttpStatusCode.NotFound);

        // A fresh process reads every persisted record back from Redis with the same outcomes.
        await server.RestartAsync();
        await replay.ExpectAsync("restart-survivor-accepted", HttpMethod.Get, KeysPath, survivor.Secret, HttpStatusCode.OK);
        await replay.ExpectAsync("restart-scoped-still-refused", HttpMethod.Get, KeysPath, scoped.Secret, HttpStatusCode.Forbidden);
        await replay.ExpectAsync("restart-revoked-still-denied", HttpMethod.Get, KeysPath, rotated.Secret, HttpStatusCode.Unauthorized);
        await replay.ExpectAsync("restart-expired-still-denied", HttpMethod.Get, KeysPath, shortLived.Secret, HttpStatusCode.Unauthorized);
        var afterRestart = await replay.ListAsync("list-after-restart");
        afterRestart.Records[scoped.Id].Status.Should().Be("active");
        afterRestart.Records[scoped.Id].ExpiresAt.Should().Be(longExpiry);
        afterRestart.Records[operatorKey.Id].Status.Should().Be("revoked");
        afterRestart.Records[operatorKey.Id].RotatedAt.Should().NotBeNull();
        afterRestart.Records[shortLived.Id].Status.Should().Be("expired");
    }

    private static string CreateBody(string name, string[] permissions, DateTimeOffset expiresAt)
        => JsonSerializer.Serialize(new { name, permissions, expiresAt });

    private static ManagedKey ParseCreated(string body)
    {
        using var document = JsonDocument.Parse(body);
        var data = document.RootElement.GetProperty("data");
        var apiKey = data.GetProperty("apiKey");
        return new ManagedKey(
            apiKey.GetProperty("id").GetGuid(),
            data.GetProperty("key").GetString()!,
            apiKey.GetProperty("permissions").EnumerateArray().Select(static item => item.GetString()!).ToArray(),
            apiKey.GetProperty("expiresAt").ValueKind == JsonValueKind.Null ? null : apiKey.GetProperty("expiresAt").GetDateTimeOffset());
    }

    private sealed record ManagedKey(Guid Id, string Secret, string[] Permissions, DateTimeOffset? ExpiresAt);

    private sealed record RecordSummary(string Status, DateTimeOffset? ExpiresAt, DateTimeOffset? LastUsedAt, DateTimeOffset? RotatedAt, DateTimeOffset? RevokedAt);

    private sealed record Listing(string Body, IReadOnlyDictionary<Guid, RecordSummary> Records);

    private sealed record EffectivePermissions(string Status, bool CanAuthenticate, string[] Permissions);

    private sealed record ImageIdentity(string Reference, string Id, IReadOnlyList<string> RepoDigests, string Revision, string Compilation);

    private sealed record Step(string Name, string Method, string Path, int? Status, IReadOnlyDictionary<int, int>? Statuses, DateTimeOffset At);

    private sealed record Receipt(
        string Schema,
        ImageIdentity Candidate,
        string AspNetCoreEnvironment,
        DateTimeOffset StartedAt,
        DateTimeOffset CompletedAt,
        int ReflectionFailuresInServerLog,
        IReadOnlyList<Step> Steps);

    private sealed class Replay(string baseUrl, ITestOutputHelper output) : IDisposable
    {
        private static readonly JsonSerializerOptions ReceiptJsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

        private readonly HttpClient _client = new() { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
        private readonly ConcurrentQueue<Step> _steps = new();

        public async Task<(HttpStatusCode Status, string Body)> SendAsync(string step, HttpMethod method, string path, string credential, string? jsonBody = null)
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Add("X-API-Key", credential);
            if (jsonBody is not null)
            {
                request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            }

            using var response = await _client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            Record(new Step(step, method.Method, path, (int)response.StatusCode, null, DateTimeOffset.UtcNow));
            return (response.StatusCode, body);
        }

        public async Task<string> ExpectAsync(string step, HttpMethod method, string path, string credential, HttpStatusCode expected, string? jsonBody = null)
        {
            var (status, body) = await SendAsync(step, method, path, credential, jsonBody);
            status.Should().Be(expected, $"step '{step}' ({method} {path}) answered: {Truncate(body)}");
            return body;
        }

        public async Task<ManagedKey> CreateKeyAsync(string step, string name, string[] permissions, DateTimeOffset expiresAt)
            => ParseCreated(await ExpectAsync(step, HttpMethod.Post, KeysPath, CandidateServer.BootstrapPassword, HttpStatusCode.Created, CreateBody(name, permissions, expiresAt)));

        public async Task<ManagedKey> RotateAsync(string step, ManagedKey key)
        {
            var rotated = ParseCreated(await ExpectAsync(step, HttpMethod.Post, $"{KeysPath}/{key.Id}/rotate", CandidateServer.BootstrapPassword, HttpStatusCode.OK));
            rotated.Id.Should().Be(key.Id);
            rotated.Secret.Should().NotBe(key.Secret);
            return rotated;
        }

        public async Task<Listing> ListAsync(string step)
        {
            var body = await ExpectAsync(step, HttpMethod.Get, KeysPath, CandidateServer.BootstrapPassword, HttpStatusCode.OK);
            using var document = JsonDocument.Parse(body);
            var records = document.RootElement.GetProperty("data").EnumerateArray().ToDictionary(
                static item => item.GetProperty("id").GetGuid(),
                static item => new RecordSummary(
                    item.GetProperty("status").GetString()!,
                    OptionalTimestamp(item, "expiresAt"),
                    OptionalTimestamp(item, "lastUsedAt"),
                    OptionalTimestamp(item, "rotatedAt"),
                    OptionalTimestamp(item, "revokedAt")));
            return new Listing(body, records);
        }

        public async Task<EffectivePermissions> EffectivePermissionsAsync(string step, Guid id)
        {
            var body = await ExpectAsync(step, HttpMethod.Get, $"{KeysPath}/{id}/effective-permissions", CandidateServer.BootstrapPassword, HttpStatusCode.OK);
            using var document = JsonDocument.Parse(body);
            var data = document.RootElement.GetProperty("data");
            return new EffectivePermissions(
                data.GetProperty("status").GetString()!,
                data.GetProperty("canAuthenticate").GetBoolean(),
                data.GetProperty("permissions").EnumerateArray().Select(static item => item.GetString()!).ToArray());
        }

        public async Task<IReadOnlyDictionary<int, int>> BurstAsync(string step, string credential, int requests)
        {
            var statuses = await Task.WhenAll(Enumerable.Range(0, requests).Select(async _ =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, KeysPath);
                request.Headers.Add("X-API-Key", credential);
                using var response = await _client.SendAsync(request);
                return (int)response.StatusCode;
            }));
            return RecordTally(step, statuses);
        }

        public TrafficLoop StartTraffic(string credential) => new(_client, credential);

        public async Task<IReadOnlyDictionary<int, int>> StopTrafficAsync(string step, TrafficLoop traffic)
            => RecordTally(step, await traffic.StopAsync());

        public async Task WriteReceiptAsync(ImageIdentity image, DateTimeOffset startedAt, int reflectionFailures, string? receiptDirectory)
        {
            var receipt = new Receipt(
                "honua.candidate-managed-api-key-receipt/v1",
                image,
                CandidateServer.Environment,
                startedAt,
                DateTimeOffset.UtcNow,
                reflectionFailures,
                _steps.ToArray());
            var json = JsonSerializer.Serialize(receipt, ReceiptJsonOptions);
            output.WriteLine(json);
            if (!string.IsNullOrWhiteSpace(receiptDirectory))
            {
                Directory.CreateDirectory(receiptDirectory);
                await File.WriteAllTextAsync(Path.Join(receiptDirectory, "managed-api-key-lifecycle.json"), json);
            }
        }

        public void Dispose() => _client.Dispose();

        private SortedDictionary<int, int> RecordTally(string step, IEnumerable<int> statuses)
        {
            var tally = new SortedDictionary<int, int>(statuses.GroupBy(static status => status).ToDictionary(static group => group.Key, static group => group.Count()));
            Record(new Step(step, "GET", KeysPath, null, tally, DateTimeOffset.UtcNow));
            return tally;
        }

        private void Record(Step step)
        {
            _steps.Enqueue(step);
            var statuses = step.Statuses is null ? string.Empty : string.Join(',', step.Statuses.Select(static pair => $"{pair.Key}x{pair.Value}"));
            output.WriteLine($"{step.At:O} {step.Name} {step.Method} {step.Path} {step.Status?.ToString(CultureInfo.InvariantCulture)}{statuses}");
        }

        private static DateTimeOffset? OptionalTimestamp(JsonElement item, string property)
            => item.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetDateTimeOffset() : null;

        private static string Truncate(string body) => body.Length <= 400 ? body : body[..400];
    }

    private sealed class TrafficLoop : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentQueue<int> _statuses = new();
        private readonly Task[] _loops;

        public TrafficLoop(HttpClient client, string credential)
        {
            _loops = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, KeysPath);
                    request.Headers.Add("X-API-Key", credential);
                    try
                    {
                        using var response = await client.SendAsync(request, _stop.Token);
                        _statuses.Enqueue((int)response.StatusCode);
                    }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                    {
                        break;
                    }
                }
            })).ToArray();
        }

        public async Task<int[]> StopAsync()
        {
            // Keep the pressure on briefly after the write lands so the recorded statuses cover both sides of it.
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            await _stop.CancelAsync();
            await Task.WhenAll(_loops);
            _statuses.Should().NotBeEmpty("the concurrent traffic must actually have run");
            return _statuses.ToArray();
        }

        public void Dispose()
        {
            _stop.Cancel();
            _stop.Dispose();
        }
    }

    private sealed class CandidateServer : IAsyncDisposable
    {
        internal const string BootstrapPassword = "Managed-Key-4571!bootstrap";
        internal const string Environment = "Production";

        private const string PostgresImage = "postgis/postgis:18-3.6";
        private const string RedisImage = "redis:7.2-alpine";
        private const string DatabasePassword = "managed-key-4571";

        private readonly string _network;
        private readonly string _serverName;
        private readonly string[] _containers;

        private CandidateServer(string network, string serverName, string[] containers, int port)
        {
            _network = network;
            _serverName = serverName;
            _containers = containers;
            BaseUrl = $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}";
        }

        public string BaseUrl { get; }

        public static async Task<CandidateServer> StartAsync(ImageIdentity image, ITestOutputHelper output)
        {
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var network = $"honua-mk-{suffix}";
            var postgres = $"{network}-pg";
            var redis = $"{network}-redis";
            var serverName = $"{network}-server";
            var port = LocalSubstrateDockerFixture.GetFreeTcpPort();
            var server = new CandidateServer(network, serverName, [serverName, redis, postgres], port);
            try
            {
                await Docker.RunCheckedAsync(["network", "create", network]);
                await Docker.RunCheckedAsync(
                [
                    "run", "-d", "--name", postgres, "--network", network,
                    "-e", "POSTGRES_USER=honua", "-e", $"POSTGRES_PASSWORD={DatabasePassword}", "-e", "POSTGRES_DB=honua",
                    PostgresImage
                ]);
                // The registry configuration the server's durability attestation expects.
                await Docker.RunCheckedAsync(
                [
                    "run", "-d", "--name", redis, "--network", network, RedisImage,
                    "redis-server", "--appendonly", "yes", "--appendfsync", "everysec", "--maxmemory-policy", "noeviction"
                ]);
                await WaitForPostgresAsync(postgres);

                await Docker.RunCheckedAsync(
                [
                    "run", "-d", "--name", serverName, "--network", network,
                    "-p", $"127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}:8080",
                    "-e", $"ASPNETCORE_ENVIRONMENT={Environment}",
                    "-e", $"ConnectionStrings__DefaultConnection=Host={postgres};Database=honua;Username=honua;Password={DatabasePassword}",
                    "-e", $"ConnectionStrings__Redis={redis}:6379",
                    "-e", $"HONUA_ADMIN_PASSWORD={BootstrapPassword}",
                    "-e", "HostValidation__AllowedHosts__0=127.0.0.1",
                    "-e", "HostValidation__AllowedHosts__1=localhost",
                    "-e", "Security__ConnectionEncryption__MasterKey=managed-key-4571-master-key-0123456789abcdef",
                    "-e", "Security__ConnectionEncryption__Salt=bWFuYWdlZC1rZXktNDU3MS1zYWx0",
                    image.Reference
                ]);
                await server.WaitForReadyAsync();
                output.WriteLine($"Candidate {image.Reference} ({image.Id}, revision {image.Revision}) ready at {server.BaseUrl}.");
                return server;
            }
            catch
            {
                output.WriteLine(await server.LogsAsync());
                await server.DisposeAsync();
                throw;
            }
        }

        public async Task RestartAsync()
        {
            await Docker.RunCheckedAsync(["restart", _serverName]);
            await WaitForReadyAsync();
        }

        public async Task<int> CountLogOccurrencesAsync(string text)
        {
            var logs = await LogsAsync();
            var count = 0;
            for (var index = logs.IndexOf(text, StringComparison.Ordinal); index >= 0; index = logs.IndexOf(text, index + text.Length, StringComparison.Ordinal))
            {
                count++;
            }

            return count;
        }

        public async ValueTask DisposeAsync()
        {
            await Docker.RunAsync(["rm", "-f", .. _containers]);
            await Docker.RunAsync(["network", "rm", _network]);
        }

        private async Task<string> LogsAsync()
        {
            var (_, stdout, stderr) = await Docker.RunAsync(["logs", _serverName]);
            return stdout + stderr;
        }

        private async Task WaitForReadyAsync()
        {
            using var client = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(5) };
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(3);
            while (DateTimeOffset.UtcNow < deadline)
            {
                try
                {
                    using var response = await client.GetAsync("/healthz/ready");
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        return;
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    // Still starting.
                }

                await Task.Delay(TimeSpan.FromSeconds(1));
            }

            throw new TimeoutException($"Candidate server '{_serverName}' did not become ready: {await LogsAsync()}");
        }

        private static async Task WaitForPostgresAsync(string name)
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(3);
            while (DateTimeOffset.UtcNow < deadline)
            {
                // The image answers pg_isready during its init phase; wait for the final server too.
                var (_, logs, logErrors) = await Docker.RunAsync(["logs", name]);
                var initialized = (logs + logErrors).Contains("PostgreSQL init process complete", StringComparison.Ordinal);
                if (initialized && (await Docker.RunAsync(["exec", name, "pg_isready", "-h", "127.0.0.1", "-U", "honua", "-d", "honua"])).ExitCode == 0)
                {
                    // Schema floors need both extensions before the first boot migrates.
                    await Docker.RunCheckedAsync(
                    [
                        "exec", name, "psql", "-U", "honua", "-d", "honua", "-v", "ON_ERROR_STOP=1",
                        "-c", "CREATE EXTENSION IF NOT EXISTS postgis", "-c", "CREATE EXTENSION IF NOT EXISTS postgis_raster"
                    ]);
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(1));
            }

            throw new InvalidOperationException($"PostGIS container '{name}' did not become ready.");
        }
    }

    private static class Docker
    {
        public static async Task<ImageIdentity> ResolveImageAsync(string reference)
        {
            const string Format = "{{.Id}}|{{join .RepoDigests \",\"}}|{{index .Config.Labels \"org.opencontainers.image.revision\"}}|{{index .Config.Labels \"honua.runtime.compilation\"}}";
            var (exitCode, stdout, _) = await RunAsync(["image", "inspect", "-f", Format, reference]);
            if (exitCode != 0)
            {
                await RunCheckedAsync(["pull", "-q", reference]);
                (exitCode, stdout, _) = await RunAsync(["image", "inspect", "-f", Format, reference]);
            }

            exitCode.Should().Be(0, $"image '{reference}' must resolve to an immutable id");
            var parts = stdout.Trim().Split('|');
            return new ImageIdentity(reference, parts[0], parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries), parts[2], parts[3]);
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

            try
            {
                process.Start();
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                return (-1, string.Empty, ex.Message);
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();
            return (process.ExitCode, stdout.ToString(), stderr.ToString());
        }
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Capabilities;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Health;
using Honua.FileStorage;
using Honua.Infrastructure.Monitoring;
using Honua.Server.Features.HealthCheck;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Geoprocessing;

public sealed class GeoprocessingOutputStoreAttestationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("honua-gp-attestation-").FullName;

    [UnitTest]
    public void Create_DeploymentToolDigest_MatchesIndependentPowerShellVector()
    {
        var options = new GeoprocessingOutputStagingOptions
        {
            StoreReference = "gp-outputs",
            PersistenceClass = "shared-persistent",
            BackupIdentity = "qualification-backup",
            BackupStoreReferences = ["gp-outputs"],
            MaxInlineArtifactBytes = 1024,
        };
        GeoprocessingOutputStoreAttestation.Create(options).ConfigurationDigest.Should()
            .Be("6eb07467421c0a70d34ef40a20aeb7f0767def7ba74cddb8b0c01d62db5b6103");
    }

    [UnitTest]
    public async Task Startup_UnattestedEphemeralDirectory_FailsWithoutCreatingStore()
    {
        var missingRoot = Path.Join(_root, "ephemeral");
        using var host = BuildHost(new Dictionary<string, string?>
        {
            ["Geoprocessing:OutputStaging:Enabled"] = "true",
            ["Geoprocessing:OutputStaging:LocalRootPath"] = missingRoot,
        });
        var start = () => host.StartAsync();
        await start.Should().ThrowAsync<OptionsValidationException>().WithMessage("*attestation*");
        Directory.Exists(missingRoot).Should().BeFalse();
    }

    [Theory]
    [InlineData("StoreReference", "different-store")]
    [InlineData("ConfigurationDigest", "bad-digest")]
    [InlineData("PersistenceClass", "ephemeral")]
    [InlineData("PersistenceClass", "")]
    [InlineData("BackupIdentity", "different-backup")]
    [InlineData("BackupIdentity", "")]
    [InlineData("BackupStoreReferences:0", "unrelated-store")]
    [InlineData("KeyPrefix", "other/outputs")]
    [InlineData("MaxInlineArtifactBytes", "2048")]
    [InlineData("ReadLeaseDuration", "00:02:00")]
    [InlineData("SweepGrace", "02:00:00")]
    [InlineData("OrphanRetention", "8.00:00:00")]
    public async Task Startup_ProducerConsumerConfigurationDiffers_FailsClosed(string field, string value)
    {
        var options = Attest();
        var configuration = GeoprocessingOutputStoreTestHelper.Configuration(options);
        configuration["Geoprocessing:OutputStaging:" + field] = value;
        using var host = BuildHost(configuration);
        var start = () => host.StartAsync();
        await start.Should().ThrowAsync<OptionsValidationException>().WithMessage("*attestation*");
    }

    [UnitTest]
    public async Task Startup_RecomputedDigestCannotOverrideVolumeIdentity()
    {
        var options = Attest();
        options.BackupIdentity = "another-backup";
        options.ConfigurationDigest = GeoprocessingOutputStoreAttestation.Create(options).ConfigurationDigest;
        using var host = BuildHost(GeoprocessingOutputStoreTestHelper.Configuration(options));
        var start = () => host.StartAsync();
        await start.Should().ThrowAsync<OptionsValidationException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("{invalid")]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task Startup_MissingOrMalformedVolumeContract_FailsClosed(string marker)
    {
        var options = Attest();
        File.WriteAllText(Path.Join(_root, GeoprocessingOutputStoreAttestation.FileName), marker);
        using var host = BuildHost(GeoprocessingOutputStoreTestHelper.Configuration(options));
        var start = () => host.StartAsync();
        await start.Should().ThrowAsync<OptionsValidationException>();
    }

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Readiness_ExposesCredentialFreeEvidenceAndDetectsLostMount(bool durableRedis)
    {
        var options = Attest();
        using var host = BuildHost(GeoprocessingOutputStoreTestHelper.Configuration(options));
        await host.StartAsync();
        var health = host.Services.GetRequiredService<HealthCheckService>();
        var migrations = new MigrationState();
        migrations.MarkSucceeded();
        var readiness = new ReadinessCheckService(new MockHealthyDatabaseChecker(), migrations,
            NullLogger<ReadinessCheckService>.Instance,
            outputStoreHealth: host.Services.GetRequiredService<GeoprocessingOutputStoreHealthCheck>(),
            durableJobSubstrateOptions: Options.Create(new DurableJobSubstrateOptions
            {
                RedisConfigured = durableRedis,
                RedisEntitled = durableRedis,
                RedisDurabilityAttestation = durableRedis
                    ? new RedisDurabilityAttestation(
                        "redis:6379", "aof_enabled=1", "appendfsync=always", "noeviction", DateTimeOffset.UtcNow)
                    : null
            }));
        (await readiness.CheckReadinessAsync()).IsReady.Should().BeTrue();
        var healthy = await health.CheckHealthAsync();
        healthy.Status.Should().Be(HealthStatus.Healthy);
        healthy.Entries["gp-output-store"].Data.Should().BeEquivalentTo(new Dictionary<string, object>
        {
            ["provider"] = "local",
            ["storeReference"] = "gp-outputs",
            ["configurationDigest"] = options.ConfigurationDigest!,
            ["persistenceClass"] = "shared-persistent",
            ["backupIdentity"] = "qualification-backup",
        });
        JsonSerializer.Serialize(healthy.Entries["gp-output-store"].Data).Should().NotContain(_root);
        var store = host.Services.GetRequiredService<IGeoprocessingOutputObjectStore>();
        File.Delete(Path.Join(_root, GeoprocessingOutputStoreAttestation.FileName));
        var unhealthy = await health.CheckHealthAsync();
        unhealthy.Status.Should().Be(HealthStatus.Unhealthy);
        unhealthy.Entries["gp-output-store"].Data.Should().BeEmpty();
        unhealthy.Entries["gp-output-store"].Exception.Should().BeNull();
        var notReady = await readiness.CheckReadinessAsync();
        notReady.StatusCode.Should().Be(503);
        notReady.Message.Should().Be("Not Ready - Referenced output store attestation unavailable");
        notReady.ReasonCode.Should().Be(ReadinessReasonCodes.GeoprocessingOutputStoreAttestationUnavailable);
        var read = () => store.OpenReadAsync("gp/outputs/job/a1/result/value.bin");
        await read.Should().ThrowAsync<InvalidOperationException>().WithMessage("*attestation*");
        await host.StopAsync();
    }

    [UnitTest]
    public async Task SharedVolume_ReplacedWorkerAndServer_ReadOriginalBytesAndChecksum()
    {
        var options = Attest();
        var configuration = GeoprocessingOutputStoreTestHelper.Configuration(options);
        // NIST SHA-256 test vector, independent of the store's computed output.
        var payload = "abc"u8.ToArray();
        const string checksum = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
        using (var worker = BuildHost(configuration))
        {
            await worker.StartAsync();
            var store = worker.Services.GetRequiredService<IGeoprocessingOutputObjectStore>();
            using var input = new MemoryStream(payload);
            var identity = await store.WriteAsync("gp/outputs/job/a1/result/value.bin", input, "application/octet-stream");
            identity.SizeBytes.Should().Be(3);
            identity.MediaType.Should().Be("application/octet-stream");
            identity.Checksum!.Value.Should().Be(checksum);
            await worker.StopAsync();
        }
        using var server = BuildHost(configuration);
        await server.StartAsync();
        var replacement = server.Services.GetRequiredService<IGeoprocessingOutputObjectStore>();
        await using var read = await replacement.OpenReadAsync("gp/outputs/job/a1/result/value.bin");
        using var buffer = new MemoryStream();
        await read!.CopyToAsync(buffer);
        buffer.ToArray().Should().Equal(payload);
        await server.StopAsync();
    }

    private GeoprocessingOutputStagingOptions Attest()
        => GeoprocessingOutputStoreTestHelper.Attest(new() { Enabled = true, LocalRootPath = _root });

    private static IHost BuildHost(Dictionary<string, string?> configuration)
        => new HostBuilder().ConfigureServices(services =>
            services.AddGeoprocessingOutputStaging(new ConfigurationBuilder().AddInMemoryCollection(configuration).Build())).Build();

    public void Dispose() => Directory.Delete(_root, recursive: true);
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GeoprocessingOutputStoreReadinessCollectionDefinition
{
    // Host startup reads staging configuration from process environment variables, exactly
    // like the container topology; no other host may be built while they are set.
    public const string Name = "Geoprocessing.OutputStoreReadiness";
}

/// <summary>
/// honua-server#4805: the served <c>/healthz/ready</c> probe of a host composed by
/// <c>Program.cs</c> (not a hand-built <see cref="ReadinessCheckService"/>) must fail closed once
/// the persistent-store attestation marker is lost, while liveness stays 200.
/// </summary>
[Collection(GeoprocessingOutputStoreReadinessCollectionDefinition.Name)]
[Protocol(TestProtocols.Health)]
public sealed class GeoprocessingOutputStoreReadinessEndpointTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("honua-gp-readiness-").FullName;

    [IntegrationTest]
    [Operation(Operations.ReadinessCheck)]
    [Endpoint("GET /healthz/ready")]
    [Endpoint("GET /healthz/live")]
    public Task ReadinessProbe_AttestationMarkerRemoved_FailsClosedAndLivenessUnaffected()
        => AssertMarkerLossFailsClosedAsync("Test", marker => File.Delete(marker));

    [IntegrationTest]
    [Operation(Operations.ReadinessCheck)]
    [Endpoint("GET /healthz/ready")]
    [Endpoint("GET /healthz/live")]
    public Task ReadinessProbe_AttestationMarkerCorrupted_FailsClosedAndLivenessUnaffected()
        => AssertMarkerLossFailsClosedAsync("Test", marker => File.WriteAllText(marker, "{\"ConfigurationDigest\":\"corrupted\""));

    [IntegrationTest]
    [Operation(Operations.ReadinessCheck)]
    [Endpoint("GET /healthz/ready")]
    [Endpoint("GET /healthz/live")]
    public Task ReadinessProbe_DevelopmentTopology_AttestationMarkerRemoved_FailsClosed()
        => AssertMarkerLossFailsClosedAsync("Development", marker => File.Delete(marker));

    private async Task AssertMarkerLossFailsClosedAsync(string environmentName, Action<string> loseMarker)
    {
        var options = GeoprocessingOutputStoreTestHelper.Attest(new() { Enabled = true, LocalRootPath = _root });
        var configuration = GeoprocessingOutputStoreTestHelper.Configuration(options);
        // The #4770 candidate topology grants Pro, which entitles the output-cache middleware.
        // Its base policy stored the anonymous 200 readiness answer for the default 60 s, so
        // store loss stayed invisible to probes until the entry expired.
        configuration["Licensing:DevGrantEdition"] = "Pro";
        using var factory = TestWebApplicationFactory.CreateForEnvironment(environmentName);
        using var client = CreateClientWithEnvironment(factory, configuration);

        var ready = await client.GetAsync("/healthz/ready");
        ready.StatusCode.Should().Be(HttpStatusCode.OK, "the attested store must not block readiness");
        (await ready.Content.ReadAsStringAsync()).Should().Be("Ready");
        ready.Headers.Contains(ReadinessReasonCodes.HeaderName).Should().BeFalse();
        AssertNotStored(ready);

        loseMarker(Path.Join(_root, GeoprocessingOutputStoreAttestation.FileName));

        // The #4770 reproduction probed six times; every probe after the loss must fail closed.
        for (var probe = 0; probe < 6; probe++)
        {
            var notReady = await client.GetAsync("/healthz/ready");
            notReady.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            (await notReady.Content.ReadAsStringAsync()).Should().Be("Not Ready");
            notReady.Headers.GetValues(ReadinessReasonCodes.HeaderName).Should()
                .Equal(ReadinessReasonCodes.GeoprocessingOutputStoreAttestationUnavailable);
            AssertNotStored(notReady);
        }

        for (var probe = 0; probe < 2; probe++)
        {
            var live = await client.GetAsync("/healthz/live");
            live.StatusCode.Should().Be(HttpStatusCode.OK);
            (await live.Content.ReadAsStringAsync()).Should().Be("Healthy");
            live.Headers.Contains(ReadinessReasonCodes.HeaderName).Should().BeFalse();
            AssertNotStored(live);
        }
    }

    private static void AssertNotStored(HttpResponseMessage response)
    {
        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        response.Headers.Age.Should().BeNull("a probe answer must never be replayed from the output cache");
    }

    private static HttpClient CreateClientWithEnvironment(
        TestWebApplicationFactory factory, IReadOnlyDictionary<string, string?> configuration)
    {
        var variables = configuration.ToDictionary(
            entry => entry.Key.Replace(":", "__", StringComparison.Ordinal), entry => entry.Value);
        var previous = variables.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var (key, value) in variables)
            {
                Environment.SetEnvironmentVariable(key, value);
            }

            return factory.CreateClient();
        }
        finally
        {
            foreach (var (key, value) in previous)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}

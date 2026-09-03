// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Capabilities;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.FileStorage;
using Honua.Infrastructure.Monitoring;
using Honua.Server.Features.HealthCheck;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
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
                    ? new RedisDurabilityAttestation("localhost:6379", "aof", "appendfsync=always",
                        "noeviction", DateTimeOffset.UtcNow)
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
        (await readiness.CheckReadinessAsync()).StatusCode.Should().Be(503);
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

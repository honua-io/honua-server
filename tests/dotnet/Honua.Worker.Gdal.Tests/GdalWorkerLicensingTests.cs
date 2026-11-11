// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Licensing;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Honua.Worker.Gdal.Tests;

[Collection(WebAppFixtureJobSubstrateExtensions.JobSubstrateCollection)]
[Protocol(ProtocolNames.Infrastructure)]
[Operation(Operations.TestInfrastructure)]
public sealed class GdalWorkerLicensingTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [IntegrationTheory]
    [InlineData(null)]
    [InlineData("not-a-signed-license")]
    public async Task AddGdalWorker_DisabledProduction_StartsWithoutLoadingLegacyLicense(string? licenseContent)
    {
        await postgres.DataSource.ClearJobSubstrateAsync();
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Production
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = postgres.ConnectionString,
            ["Licensing:Mode"] = "Disabled",
            ["Licensing:Edition"] = "Enterprise",
            ["Licensing:LicensePath"] = "/nonexistent/disabled-worker/license.json",
            ["Licensing:LicenseContent"] = licenseContent,
            ["Licensing:LicenseContentSecretRef"] = null
        });
        builder.Services.AddGdalWorker(builder.Configuration);
        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            host.Services.GetRequiredService<IHostEnvironment>().IsProduction().Should().BeTrue();
            var policy = host.Services.GetRequiredService<ILicenseOperationPolicy>();
            policy.IsBlocked.Should().BeFalse();
            policy.OperationCancellation.CanBeCanceled.Should().BeFalse();
            host.Services.GetServices<IHostedService>().Should().NotContain(service => ReferenceEquals(service, policy));
            host.Services.GetServices<ILicenseContentSecretResolver>().Should().BeEmpty();
            var snapshot = host.Services.GetRequiredService<ILicenseEntitlementService>().GetSnapshot();
            snapshot.Mode.Should().Be(LicenseMode.Disabled);
            snapshot.Entitlements.Should().HaveCount(FeatureCatalog.All.Count).And.OnlyContain(entitlement => entitlement.IsActive);
            var capacity = await host.Services.GetRequiredService<ILicenseCapacityMeter>().GetCapacityStateAsync();
            capacity.MeteringEnabled.Should().BeFalse();
            capacity.LiveInstanceCount.Should().Be(0);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [IntegrationTest]
    public async Task AssignedWorker_TerminalOperation_ExitsWithoutClaimingQueuedWork()
    {
        await postgres.DataSource.ClearJobSubstrateAsync();
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Development
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = postgres.ConnectionString,
            ["Licensing:Mode"] = "Disabled",
        });
        builder.Services.AddGdalWorker(builder.Configuration);
        var operationId = $"native-assigned-{Guid.NewGuid():N}";
        builder.Services.AddAssignedExecutionWorker(new ExecutionWorkerLaunch
        {
            OperationId = operationId,
            JobKind = ExecutionJobKind.Geoprocessing,
            RuntimeProfile = "native",
            ExecutionAttempt = 1,
        });
        var host = builder.Build();
        var store = host.Services.GetRequiredService<IExecutionJobStore>();
        var now = DateTimeOffset.UtcNow;
        var terminal = new ExecutionJobRecord
        {
            OperationId = operationId,
            Status = ExecutionJobStatus.Succeeded,
            CreatedAt = now,
            UpdatedAt = now,
            AttemptCount = 1,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "honua-kubernetes-job",
                WorkloadName = "native",
                RuntimeProfile = "native",
            },
        };
        await store.SetAsync(terminal);
        var unrelatedId = $"unrelated-{Guid.NewGuid():N}";
        (await store.TryCreateAsync(terminal with
        {
            OperationId = unrelatedId,
            Status = ExecutionJobStatus.Queued,
            AttemptCount = 0,
            Spec = terminal.Spec with { TargetKind = BatchComputeTargetKind.LocalProcess, Backend = "local" },
        })).Should().BeTrue();
        var sharedDatabase = new JobStoreDatabase(postgres.DataSource);
        var sharedQueue = new PostgresJobQueue(sharedDatabase, NullLogger<PostgresJobQueue>.Instance);
        await sharedQueue.EnqueueAsync(unrelatedId);
        (await sharedQueue.GetQueueDepthAsync()).Should().Be(1);

        host.Services.GetRequiredService<IJobQueue>().Should().BeOfType<AssignedExecutionJobQueue>();
        host.Services.GetServices<IHostedService>().Should().NotContain(service => service is JobReconciliationService);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var exitCode = await host.RunExecutionWorkerAsync(timeout.Token);

        timeout.IsCancellationRequested.Should().BeFalse("the assigned worker must stop itself");
        exitCode.Should().Be(0);
        var sharedStore = new PostgresExecutionJobStore(
            sharedDatabase, Options.Create(new JobStoreOptions()), NullLogger<PostgresExecutionJobStore>.Instance);
        var unrelated = (await sharedStore.GetAsync(unrelatedId))!;
        unrelated.Status.Should().Be(ExecutionJobStatus.Queued);
        unrelated.ClaimedBy.Should().BeNull();
        unrelated.AttemptCount.Should().Be(0);
        (await sharedQueue.GetQueueDepthAsync()).Should().Be(1);
    }

    [UnitTest]
    public void AddGdalWorker_RegistersLicensePolicyAndBothCloudSecretResolvers()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=unused"
            }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGdalWorker(configuration);
        using var provider = services.BuildServiceProvider();

        var policy = provider.GetRequiredService<ILicenseOperationPolicy>();
        policy.Should().BeAssignableTo<IHostedService>();
        policy.IsBlocked.Should().BeFalse();
        var resolvers = provider.GetServices<ILicenseContentSecretResolver>().ToArray();
        resolvers.Should().Contain(resolver => resolver.CanResolve("aws:secretsmanager:arn:aws:secretsmanager:us-east-1:000000000000:secret:synthetic"));
        resolvers.Should().Contain(resolver => resolver.CanResolve("azure:keyvault:https://synthetic.vault.azure.net/secrets/synthetic"));
    }
}

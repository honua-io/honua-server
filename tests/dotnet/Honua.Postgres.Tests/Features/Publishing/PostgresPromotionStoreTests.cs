// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Deployment.Abstractions;
using Honua.Core.Features.Deployment.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Publishing.Abstractions;
using Honua.Core.Features.Publishing.Domain;
using Honua.Postgres.Features.Publishing;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Honua.Postgres.Tests.Features.Publishing;

/// <summary>
/// Durable Postgres proof for the MCP promotion resource stores (#2482).
/// </summary>
[Collection("Database")]
public sealed class PostgresPromotionStoreTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task PublishedServiceStore_RoundTripsAcrossInstances_AndFiltersLifecycleAndSource()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresPromotionStoreTests) + "_Published");
        try
        {
            await EnsureTablesAsync(schema);
            var writer = new PostgresPublishedServiceStore(fixture.DataSource, schema);
            var reader = new PostgresPublishedServiceStore(fixture.DataSource, schema);
            var service = BuildPublishedService("service-a", "result-1");

            (await writer.TryCreateAsync(service)).Should().BeTrue();
            (await writer.TryCreateAsync(service)).Should().BeFalse();

            var stored = await reader.GetAsync(service.ServiceId);
            stored.Should().BeEquivalentTo(service);
            (await reader.ListActiveAsync()).Should().ContainSingle(item => item.ServiceId == service.ServiceId);
            (await reader.ListBySourceAsync("result-1")).Should().ContainSingle(item => item.ServiceId == service.ServiceId);

            var decommissioned = service with
            {
                Status = PublishedServiceStatus.Decommissioned,
                UpdatedAt = service.UpdatedAt.AddMinutes(1),
                Warnings = ["retired after promotion"]
            };
            await writer.SetAsync(decommissioned);

            (await reader.GetAsync(service.ServiceId)).Should().BeEquivalentTo(decommissioned);
            (await reader.ListActiveAsync()).Should().BeEmpty();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task DeploymentStore_RoundTripsTransitions_AndFiltersLifecycleSourceAndTarget()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresPromotionStoreTests) + "_Deployments");
        try
        {
            await EnsureTablesAsync(schema);
            var writer = new PostgresDeploymentStore(fixture.DataSource, schema);
            var reader = new PostgresDeploymentStore(fixture.DataSource, schema);
            var deployment = BuildDeployment("deployment-a", "service-a", "production");

            (await writer.TryCreateAsync(deployment)).Should().BeTrue();
            (await writer.TryCreateAsync(deployment)).Should().BeFalse();

            var stored = await reader.GetAsync(deployment.DeploymentId);
            stored.Should().BeEquivalentTo(deployment);
            stored!.Transitions.Should().HaveCount(3);
            (await reader.ListActiveAsync()).Should().ContainSingle(item => item.DeploymentId == deployment.DeploymentId);
            (await reader.ListBySourceAsync(DeploymentSourceKind.PublishedService, "service-a"))
                .Should().ContainSingle(item => item.DeploymentId == deployment.DeploymentId);
            (await reader.ListByTargetAsync("production"))
                .Should().ContainSingle(item => item.DeploymentId == deployment.DeploymentId);

            var retired = deployment.WithRetired("superseded by release");
            await writer.SetAsync(retired);

            (await reader.GetAsync(deployment.DeploymentId)).Should().BeEquivalentTo(retired);
            (await reader.ListActiveAsync()).Should().BeEmpty();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [UnitTest]
    public void AddPostgreSqlServices_RegistersDurablePromotionStores()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=honua;Username=honua;Password=honua"
            })
            .Build();

        services.AddPostgreSqlServices(configuration);

        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IPublishedServiceStore) &&
            descriptor.Lifetime == ServiceLifetime.Singleton);
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IDeploymentStore) &&
            descriptor.Lifetime == ServiceLifetime.Singleton);
    }

    private static PublishedServiceRecord BuildPublishedService(string serviceId, string sourceId)
    {
        var now = new DateTimeOffset(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);
        return new PublishedServiceRecord
        {
            ServiceId = serviceId,
            IntentId = "intent-a",
            SourceKind = PublishSourceKind.ResultPackage,
            SourceId = sourceId,
            TargetKind = PublishTargetKind.FeatureService,
            Status = PublishedServiceStatus.Active,
            Artifacts =
            [
                new ArtifactRef
                {
                    ArtifactId = "artifact-a",
                    Kind = ArtifactKind.FeatureLayer,
                    Label = "Result layer",
                    Uri = "honua://results/result-1/artifacts/artifact-a",
                    ContentType = "application/geo+json",
                    Metadata = new Dictionary<string, string> { ["table"] = "result_a" }
                }
            ],
            Endpoint = "/rest/services/service-a/FeatureServer",
            RefreshPolicy = RefreshPolicy.Manual(),
            PublishedAt = now,
            UpdatedAt = now,
            Audit = new OperationAuditInfo
            {
                RequestedBy = "operator@example.com",
                CorrelationId = "corr-a",
                IdempotencyKey = "publish-a"
            }
        };
    }

    private static Deployment BuildDeployment(string deploymentId, string serviceId, string targetId)
        => Deployment.CreateDraft(
                deploymentId,
                DeploymentSource.FromPublishedService(serviceId),
                new DeploymentTarget
                {
                    TargetId = targetId,
                    Kind = DeploymentKind.FeatureService,
                    HostingMode = HostingMode.ManagedService,
                    RoutePrefix = "/services/service-a",
                    Environment = "production",
                    Labels = new Dictionary<string, string> { ["region"] = "us-west" }
                },
                audit: new OperationAuditInfo
                {
                    RequestedBy = "operator@example.com",
                    CorrelationId = "corr-deploy"
                })
            .WithProvisioning("allocate")
            .WithRollingOut(reason: "roll out")
            .WithActive("https://example.test/services/service-a", "promote");

    private async Task EnsureTablesAsync(string schema)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE TABLE "{schema}".promotion_published_services (
                service_id TEXT PRIMARY KEY,
                intent_id TEXT NOT NULL,
                source_kind TEXT NOT NULL,
                source_id TEXT NOT NULL,
                target_kind TEXT NOT NULL,
                status TEXT NOT NULL,
                document JSONB NOT NULL,
                published_at TIMESTAMPTZ NOT NULL,
                updated_at TIMESTAMPTZ NOT NULL
            );
            CREATE TABLE "{schema}".promotion_deployments (
                deployment_id TEXT PRIMARY KEY,
                source_kind TEXT NOT NULL,
                source_id TEXT NOT NULL,
                target_id TEXT NOT NULL,
                status TEXT NOT NULL,
                document JSONB NOT NULL,
                created_at TIMESTAMPTZ NOT NULL,
                updated_at TIMESTAMPTZ NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync();
    }
}

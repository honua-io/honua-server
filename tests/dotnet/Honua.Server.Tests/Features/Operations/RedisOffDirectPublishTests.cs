// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using FluentAssertions;
using Honua.Core.Exceptions;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Capabilities;
using Honua.Core.Features.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Guardrails.Abstractions;
using Honua.Core.Features.Infrastructure.Health;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Services;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Styling.Domain;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace Honua.Server.Tests.Features.OperationsRedisOff;

/// <summary>
/// Owner decision 20 (2026-10-10): on a Production host composed without Redis, a direct
/// <c>service.publish</c> that the operation policy allows executes without the Redis-backed
/// operation-instance store, because no governed proposal is involved. The handle still carries
/// the canonical evidence the terminal journey checks (<c>operationId</c>,
/// <c>operationInstanceId</c>, <c>correlationId</c>, <c>auditId</c>) and is marked non-durable.
/// Proposal-based paths keep the typed <c>missingDependency=redis</c> refusal. Evidence:
/// honua-release run 38066103745, aws-serverless/redis-off journey stage 3.
/// </summary>
public sealed class RedisOffDirectPublishTests
{
    private const string AdminRole = "admin";
    private const string ScopedAdminRole = "scoped-admin-key";
    private const string LayerWriteKeyRole = "layer-write-key";

    [Theory]
    [InlineData(AdminRole)]
    [InlineData(ScopedAdminRole)]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public async Task RedisOffProduction_PolicyAllowedAdminPublish_ExecutesAndReturnsCanonicalHandle(string role)
    {
        var publishing = PublishingService();
        var audit = new CapturingAuditLog();
        await using var provider = ComposeRedisOffProduction(publishing, audit, AdminAllowPolicy());
        await using var scope = provider.CreateAsyncScope();
        var invoker = scope.ServiceProvider.GetRequiredService<IOperationInvoker>();

        var handle = await invoker.SubmitAsync(PublishRequest(), Caller(role));

        handle.Status.Should().Be(OperationHandleStatus.Completed, handle.Reason);
        handle.OperationId.Should().Be(ServicePublishOperation.OperationId);
        handle.OperationInstanceId.Should().StartWith("opinst-");
        handle.HandleId.Should().Be(handle.OperationInstanceId);
        handle.CorrelationId.Should().StartWith("corr-");
        handle.AuditId.Should().NotBeNullOrWhiteSpace();
        handle.PolicyDecision.Should().Be(PolicyDecisionKind.Allow);
        handle.AuthorizationOutcome.Should().Be("admin-authorized");
        handle.ProposalId.Should().BeNull("a direct publish involves no governed proposal");
        handle.EvidenceRefs.Should().Contain(NonDurableDirectOperationInstanceStore.NonDurableEvidenceRef);
        handle.Result!.Details.Should().ContainKey("layerId");
        await publishing.Received(1).PublishLayerAsync(
            Arg.Any<string>(), Arg.Any<LayerPublishRequest>(), Arg.Any<CancellationToken>());

        audit.Events.Should().Contain(e =>
            e.Action == "operation.accepted" &&
            e.ResourceId == handle.OperationInstanceId &&
            e.CorrelationId == handle.CorrelationId);
        audit.Events.Should().Contain(e =>
            e.Action == "operation.completed" &&
            e.Outcome == AuditOutcome.Success &&
            e.ResourceId == handle.OperationInstanceId &&
            e.Details.Contains("instanceRecord=non-durable", StringComparison.Ordinal));
    }

    [UnitTest]
    public async Task RedisOffProduction_LayerWriteKeyCaller_IsDeniedWithoutPublishing()
    {
        var publishing = PublishingService();
        await using var provider = ComposeRedisOffProduction(publishing, new CapturingAuditLog(), AdminAllowPolicy());
        await using var scope = provider.CreateAsyncScope();
        var invoker = scope.ServiceProvider.GetRequiredService<IOperationInvoker>();

        var handle = await invoker.SubmitAsync(PublishRequest(), Caller(LayerWriteKeyRole));

        handle.Status.Should().Be(OperationHandleStatus.Denied, handle.Reason);
        handle.PolicyDecision.Should().Be(PolicyDecisionKind.Deny);
        handle.AuditId.Should().NotBeNullOrWhiteSpace("the denied attempt is still audited");
        await publishing.DidNotReceiveWithAnyArgs().PublishLayerAsync(default!, default!, default);
    }

    [UnitTest]
    public async Task RedisOffProduction_RequireApprovalPublish_KeepsTypedRedisRefusal()
    {
        var publishing = PublishingService();
        var policy = new Dictionary<string, string?>(AdminAllowPolicy())
        {
            ["Operations:Policy:Rules:0:OperationId"] = ServicePublishOperation.OperationId,
            ["Operations:Policy:Rules:0:Role"] = null,
            ["Operations:Policy:Rules:0:Decision"] = "RequireApproval",
            ["Operations:Policy:Rules:0:ApprovalLane"] = "operator",
        };
        await using var provider = ComposeRedisOffProduction(publishing, new CapturingAuditLog(), policy);
        await using var scope = provider.CreateAsyncScope();
        var invoker = scope.ServiceProvider.GetRequiredService<IOperationInvoker>();

        var refusal = await Assert.ThrowsAsync<CapabilityUnavailableException>(
            () => invoker.SubmitAsync(PublishRequest(), Caller(AdminRole)));

        refusal.MissingDependency.Should().Be(CapabilityUnavailableCodes.RedisDependency);
        refusal.Capability.Should().Be(CapabilityUnavailableCodes.ControlPlaneProposalsCapability);
        await publishing.DidNotReceiveWithAnyArgs().PublishLayerAsync(default!, default!, default);
    }

    [UnitTest]
    public async Task RedisOffProduction_StudioPublicationRequest_KeepsTypedRedisRefusal()
    {
        await using var provider = ComposeRedisOffProduction(PublishingService(), new CapturingAuditLog(), AdminAllowPolicy());
        await using var scope = provider.CreateAsyncScope();
        var invoker = scope.ServiceProvider.GetRequiredService<IOperationInvoker>();

        var refusal = await Assert.ThrowsAsync<CapabilityUnavailableException>(() => invoker.SubmitAsync(
            new OperationRequest
            {
                OperationId = StudioDraftOperations.CreatePublicationRequest,
                Parameters = new Dictionary<string, string?>(StringComparer.Ordinal),
            },
            Caller(AdminRole)));

        refusal.MissingDependency.Should().Be(CapabilityUnavailableCodes.RedisDependency);
    }

    [UnitTest]
    public async Task RedisOffProduction_PolicyAllowedGovernedAdminCall_KeepsTypedRedisRefusal()
    {
        await using var provider = ComposeRedisOffProduction(PublishingService(), new CapturingAuditLog(), AdminAllowPolicy());
        await using var scope = provider.CreateAsyncScope();
        var invoker = scope.ServiceProvider.GetRequiredService<IOperationInvoker>();

        var refusal = await Assert.ThrowsAsync<CapabilityUnavailableException>(() => invoker.SubmitAsync(
            new OperationRequest
            {
                OperationId = "admin.layer.filter.set",
                Parameters = new Dictionary<string, string?>(StringComparer.Ordinal) { ["layerId"] = "1" },
            },
            Caller(AdminRole)));

        refusal.MissingDependency.Should().Be(CapabilityUnavailableCodes.RedisDependency);
    }

    [Theory]
    [InlineData(AdminRole)]
    [InlineData(ScopedAdminRole)]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public async Task RedisOffProduction_PolicyAllowedAdminStylePreset_ExecutesDirectlyWithCanonicalHandle(string role)
    {
        // Owner decision 3: a Redis-off cell is a serving + configure + style/render topology, so
        // terminal-journey stage 4 (style.apply-preset) must execute there. Before this change
        // honua-release run 38249563601 (aws-ecs, redis-off) refused it with
        // code=unavailable, missingDependency=redis.
        var catalog = StyleCatalog();
        var graphSync = Substitute.For<IMetadataV2StyleGraphSync>();
        var audit = new CapturingAuditLog();
        await using var provider = ComposeRedisOffProduction(
            PublishingService(), audit, AdminAllowPolicy(), catalog, graphSync);
        await using var scope = provider.CreateAsyncScope();
        var invoker = scope.ServiceProvider.GetRequiredService<IOperationInvoker>();

        var handle = await invoker.SubmitAsync(StylePresetRequest(), Caller(role));

        handle.Status.Should().Be(OperationHandleStatus.Completed, handle.Reason);
        handle.OperationId.Should().Be(StylePresetOperation.OperationId);
        handle.OperationInstanceId.Should().StartWith("opinst-");
        handle.HandleId.Should().Be(handle.OperationInstanceId);
        handle.CorrelationId.Should().StartWith("corr-");
        handle.AuditId.Should().NotBeNullOrWhiteSpace();
        handle.PolicyDecision.Should().Be(PolicyDecisionKind.Allow);
        handle.ProposalId.Should().BeNull("a direct style preset involves no governed proposal");
        handle.EvidenceRefs.Should().Contain(NonDurableDirectOperationInstanceStore.NonDurableEvidenceRef);
        await catalog.Received(1).AssociateLayerAsync(StyleStorageLayerId, StyleId, 0, Arg.Any<CancellationToken>());
        await graphSync.Received(1).SyncLayerStylesAsync(StyleStorageLayerId, Arg.Any<CancellationToken>());

        audit.Events.Should().Contain(e =>
            e.Action == "operation.accepted" &&
            e.ResourceId == handle.OperationInstanceId &&
            e.CorrelationId == handle.CorrelationId);
        audit.Events.Should().Contain(e =>
            e.Action == "operation.completed" &&
            e.Outcome == AuditOutcome.Success &&
            e.ResourceId == handle.OperationInstanceId &&
            e.Details.Contains("operationId=style.apply-preset", StringComparison.Ordinal) &&
            e.Details.Contains("instanceRecord=non-durable", StringComparison.Ordinal));
    }

    [UnitTest]
    public async Task RedisOffProduction_LayerWriteKeyStylePreset_IsDeniedWithoutApplying()
    {
        var catalog = StyleCatalog();
        await using var provider = ComposeRedisOffProduction(
            PublishingService(), new CapturingAuditLog(), AdminAllowPolicy(), catalog);
        await using var scope = provider.CreateAsyncScope();
        var invoker = scope.ServiceProvider.GetRequiredService<IOperationInvoker>();

        var handle = await invoker.SubmitAsync(StylePresetRequest(), Caller(LayerWriteKeyRole));

        handle.Status.Should().Be(OperationHandleStatus.Denied, handle.Reason);
        await catalog.DidNotReceiveWithAnyArgs().AssociateLayerAsync(default, default!, default, default);
    }

    [UnitTest]
    public async Task RedisOffProduction_RequireApprovalStylePreset_KeepsTypedRedisRefusal()
    {
        var catalog = StyleCatalog();
        var policy = new Dictionary<string, string?>(AdminAllowPolicy())
        {
            ["Operations:Policy:Rules:0:OperationId"] = StylePresetOperation.OperationId,
            ["Operations:Policy:Rules:0:Role"] = null,
            ["Operations:Policy:Rules:0:Decision"] = "RequireApproval",
            ["Operations:Policy:Rules:0:ApprovalLane"] = "operator",
        };
        await using var provider = ComposeRedisOffProduction(PublishingService(), new CapturingAuditLog(), policy, catalog);
        await using var scope = provider.CreateAsyncScope();
        var invoker = scope.ServiceProvider.GetRequiredService<IOperationInvoker>();

        var refusal = await Assert.ThrowsAsync<CapabilityUnavailableException>(
            () => invoker.SubmitAsync(StylePresetRequest(), Caller(AdminRole)));

        refusal.MissingDependency.Should().Be(CapabilityUnavailableCodes.RedisDependency);
        refusal.Capability.Should().Be(CapabilityUnavailableCodes.ControlPlaneProposalsCapability);
        await catalog.DidNotReceiveWithAnyArgs().AssociateLayerAsync(default, default!, default, default);
    }

    [Theory]
    [InlineData("control-plane.geoprocess")]
    [InlineData(StudioDraftOperations.Create)]
    [InlineData(StudioDraftOperations.CreatePublicationRequest)]
    [InlineData("admin.layer.style.set")]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public async Task RedisOffProduction_PolicyAllowedGovernedOperations_KeepTypedRedisRefusal(string operationId)
    {
        // Only the configure (service.publish) and style (style.apply-preset) steps are direct on
        // a Redis-off host. Geoprocessing, durable compositions, proposals and approvals, and the
        // governed honua_admin_* catalogs keep the typed refusal (journey stages 5-8).
        await using var provider = ComposeRedisOffProduction(
            PublishingService(), new CapturingAuditLog(), AdminAllowPolicy(), StyleCatalog());
        await using var scope = provider.CreateAsyncScope();
        var invoker = scope.ServiceProvider.GetRequiredService<IOperationInvoker>();

        var refusal = await Assert.ThrowsAsync<CapabilityUnavailableException>(() => invoker.SubmitAsync(
            new OperationRequest
            {
                OperationId = operationId,
                Parameters = new Dictionary<string, string?>(StringComparer.Ordinal) { ["layerId"] = "1" },
            },
            Caller(AdminRole)));

        refusal.MissingDependency.Should().Be(CapabilityUnavailableCodes.RedisDependency);
    }

    [UnitTest]
    public async Task NonDurableStore_RoutesOnlyDirectOperations_AndDelegatesEverythingElse()
    {
        var durable = Substitute.For<IOperationInstanceStore>();
        durable.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<OperationHandle?>(_ => throw new CapabilityUnavailableException(
                CapabilityUnavailableCodes.DurableControlPlaneDetail,
                CapabilityUnavailableCodes.RedisDependency,
                CapabilityUnavailableCodes.RedisRemediation,
                CapabilityUnavailableCodes.RedisRemediationRef));
        var store = new NonDurableDirectOperationInstanceStore(durable, [ServicePublishOperation.OperationId]);
        var direct = Envelope("opinst-direct", ServicePublishOperation.OperationId);
        var governed = Envelope("opinst-governed", "admin.layer.filter.set");

        (await store.TryCreateAsync(direct)).Should().BeTrue();
        store.IsNonDurable(direct.OperationInstanceId).Should().BeTrue();
        await store.SetAsync(direct with { Status = OperationHandleStatus.Completed });
        (await store.GetAsync(direct.OperationInstanceId))!.Status.Should().Be(OperationHandleStatus.Completed);

        await store.TryCreateAsync(governed);
        await durable.Received(1).TryCreateAsync(governed, Arg.Any<CancellationToken>());
        store.IsNonDurable(governed.OperationInstanceId).Should().BeFalse();
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() => store.GetAsync("opinst-unknown"));
    }

    [UnitTest]
    public void DispatcherInstanceStore_WrapsOnlyTheRedisOffPlaceholder()
    {
        OperationsServiceCollectionExtensions.ComposeDispatcherInstanceStore(new UnavailableOperationInstanceStore())
            .Should().BeOfType<NonDurableDirectOperationInstanceStore>()
            .Which.DirectOperationIds.Should().BeEquivalentTo(
                new[] { ServicePublishOperation.OperationId, StylePresetOperation.OperationId });
        var durable = new VolatileOperationInstanceStore();
        OperationsServiceCollectionExtensions.ComposeDispatcherInstanceStore(durable).Should().BeSameAs(durable);
    }

    private static Dictionary<string, string?> AdminAllowPolicy() => new(StringComparer.Ordinal)
    {
        ["Operations:Policy:Enabled"] = "true",
        ["Operations:Policy:DefaultDecision"] = "Deny",
        ["Operations:Policy:Rules:0:Role"] = AdminRole,
        ["Operations:Policy:Rules:0:Decision"] = "Allow",
        ["Operations:Policy:Rules:1:Role"] = ScopedAdminRole,
        ["Operations:Policy:Rules:1:Decision"] = "Allow",
    };

    private static ServiceProvider ComposeRedisOffProduction(
        ILayerPublishingService publishing,
        IAuditLog audit,
        IReadOnlyDictionary<string, string?> policy,
        IStyleCatalog? styleCatalog = null,
        IMetadataV2StyleGraphSync? styleGraphSync = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(audit);
        var readiness = Substitute.For<IReadinessCheckService>();
        readiness.CheckReadinessAsync(Arg.Any<CancellationToken>()).Returns(ReadinessResult.Ready());
        services.AddSingleton(readiness);
        services.AddSingleton(Substitute.For<IGuardrailLadder>());
        services.AddSingleton(publishing);
        var resolver = Substitute.For<ISecureConnectionResolver>();
        resolver.ResolveConnectionStringAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns("Host=localhost;Database=test");
        resolver.ResolveConnectionStringAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("Host=localhost;Database=test");
        services.AddSingleton(resolver);
        if (styleCatalog is null)
        {
            var graphProvider = Substitute.For<IMetadataV2GraphProvider>();
            graphProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(
                new MetadataV2GraphSnapshot(new MetadataV2Graph { Revision = 7 }, "\"etag\"", DateTimeOffset.UtcNow));
            services.AddSingleton(graphProvider);
        }
        else
        {
            services.AddSingleton<IMetadataV2GraphProvider>(StyleGraph());
            services.AddSingleton(styleCatalog);
            services.AddSingleton(styleGraphSync ?? Substitute.For<IMetadataV2StyleGraphSync>());
        }

        // Mirrors the Program.cs no-Redis branch: no IConnectionMultiplexer, fail-closed proposal store.
        services.AddSingleton<IOperationProposalStore, UnavailableOperationProposalStore>();
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Production);
        services.AddOperationsToolset(new ConfigurationBuilder().AddInMemoryCollection(policy).Build(), environment);
        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IOperationInstanceStore>().Should().BeOfType<UnavailableOperationInstanceStore>(
            "the host under test is the Redis-off Production composition");
        return provider;
    }

    private static ILayerPublishingService PublishingService()
    {
        var publishing = Substitute.For<ILayerPublishingService>();
        publishing
            .ValidateTableForPublishAsync(Arg.Any<string>(), Arg.Any<TablePublishValidationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TablePublishValidationResult
            {
                IsValid = true,
                Status = "valid",
                Schema = "public",
                Table = "parcels",
                ServiceName = "default",
            });
        publishing
            .PublishLayerAsync(Arg.Any<string>(), Arg.Any<LayerPublishRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PublishedLayerSummary
            {
                LayerId = 123,
                LayerName = "Parcels",
                Schema = "public",
                Table = "parcels",
                GeometryType = "Polygon",
                Srid = 4326,
                ServiceName = "default",
            });
        return publishing;
    }

    private static OperationRequest PublishRequest() => new()
    {
        OperationId = ServicePublishOperation.OperationId,
        ConnectionId = "11111111-1111-1111-1111-111111111111",
        ServiceName = "default",
        Parameters = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["schema"] = "public",
            ["table"] = "parcels",
            ["layerName"] = "Parcels",
        },
    };

    private const string StyleServiceId = "svc-parcels";
    private const int StyleLayerIndex = 0;
    private const int StyleStorageLayerId = 42;
    private const string StyleId = "style_flood_depth";

    private static IStyleCatalog StyleCatalog()
    {
        var catalog = Substitute.For<IStyleCatalog>();
        catalog.GetStyleAsync(StyleId, Arg.Any<CancellationToken>()).Returns(new StyleCatalogRecord
        {
            StyleId = StyleId,
            Title = "Flood depth",
            MapLibreStyleJson = "{\"version\":8,\"layers\":[]}",
            StyleVersion = 1,
        });
        catalog.AssociateLayerAsync(StyleStorageLayerId, StyleId, 0, Arg.Any<CancellationToken>()).Returns(true);
        return catalog;
    }

    private static TestMetadataV2GraphProvider StyleGraph()
        => new TestMetadataV2GraphBuilder()
            .AddResource("res-parcels", "Parcels Dataset", spatial: new MetadataV2ResourceSpatial
            {
                GeometryType = MetadataV2GeometryType.Polygon,
                SpatialReference = new MetadataV2SpatialReference { Srid = 4326 },
                Bbox = new MetadataV2Bbox { West = -10, South = -10, East = 10, North = 10 },
            })
            .AddStorageBinding("bind-parcels", "res-parcels", "public.parcels", storageLayerId: StyleStorageLayerId)
            .AddService(StyleServiceId, "Parcels")
            .AddPublication("pub-parcels", StyleServiceId, "res-parcels", layerIndex: StyleLayerIndex, storageBindingId: "bind-parcels")
            .BuildProvider();

    private static OperationRequest StylePresetRequest() => new()
    {
        OperationId = StylePresetOperation.OperationId,
        Parameters = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["serviceId"] = StyleServiceId,
            ["layerId"] = StyleLayerIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["styleId"] = StyleId,
        },
    };

    private static OperationPolicyContext Caller(string role) => new()
    {
        PrincipalId = $"{role}-principal",
        AuthorizationOutcome = "admin-authorized",
        Roles = [role],
    };

    private static OperationHandle Envelope(string operationInstanceId, string operationId) => new()
    {
        OperationInstanceId = operationInstanceId,
        OperationId = operationId,
        CorrelationId = $"corr-{operationInstanceId}",
        Status = OperationHandleStatus.Accepted,
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    private sealed class CapturingAuditLog : IAuditLog
    {
        private readonly ConcurrentQueue<AuditEvent> _events = new();

        public IReadOnlyCollection<AuditEvent> Events => _events;

        public Task<string?> RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            _events.Enqueue(auditEvent);
            return Task.FromResult<string?>($"audit-{Guid.NewGuid():N}");
        }
    }
}

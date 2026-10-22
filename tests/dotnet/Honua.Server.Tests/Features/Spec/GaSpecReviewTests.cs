// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Features.Workflow;
using Honua.Server.Features.Spec;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using NSubstitute;

namespace Honua.Server.Tests.Features.Spec;

[Collection("Unit")]
public sealed class GaSpecReviewTests
{
    [UnitTest]
    public async Task Apply_ConcurrentAndRestartedReplay_PublishesOnceAndReadsVerifiedState()
    {
        var harness = new Harness();
        var plan = await harness.Service.PlanAsync(harness.Spec, "tenant", "agent", CancellationToken.None);
        var command = new GaSpecApplyRequest(plan.SpecReview!.Revision, Guid.NewGuid());
        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            try { return await harness.Service.ApplyAsync(plan.OperationId, command, "tenant", "human", CancellationToken.None); }
            catch (GaSpecBlockedException) { return null; }
        }));
        Assert.Contains(attempts, result => result?.Status == WorkflowOperationStatus.Succeeded);
        Assert.Equal(1, harness.Publishes);
        var restarted = new GaSpecReviewService(harness.Store, harness.Adapter, harness.Contexts);
        var replay = await restarted.ApplyAsync(plan.OperationId, command, "tenant", "human", CancellationToken.None);
        Assert.Equal("verified", replay.SpecReview!.Evidence);
        Assert.Equal("human", replay.SpecReview.AppliedBy);
        Assert.Equal("agent", replay.SpecReview.DraftedBy);
        Assert.Equal(1, harness.Publishes);
    }

    [UnitTest]
    public async Task Expectations_ChangeVerificationHashWithoutChangingActionHash()
    {
        var harness = new Harness();
        var first = await harness.Service.PlanAsync(harness.Spec, "tenant", "agent", CancellationToken.None);
        var second = await harness.Service.PlanAsync(harness.Spec with { ExpectEnabled = true }, "tenant", "agent", CancellationToken.None);
        Assert.Equal(first.SpecReview!.ActionHash, second.SpecReview!.ActionHash);
        Assert.NotEqual(first.SpecReview.VerificationHash, second.SpecReview.VerificationHash);
    }

    [UnitTest]
    public async Task Plan_NoopAfterPublication_ReobservesDeletionBeforeApply()
    {
        var harness = new Harness();
        var first = await harness.Service.PlanAsync(harness.Spec, "tenant", "agent", CancellationToken.None);
        await harness.Service.ApplyAsync(first.OperationId, new(first.SpecReview!.Revision, Guid.NewGuid()), "tenant", "human", CancellationToken.None);
        var second = await harness.Service.PlanAsync(harness.Spec, "tenant", "agent", CancellationToken.None);
        Assert.Equal("noop", second.SpecReview!.Verdict);
        harness.Layer = null;
        var blocked = await Assert.ThrowsAsync<GaSpecBlockedException>(() => harness.Service.ApplyAsync(second.OperationId,
            new(second.SpecReview.Revision, Guid.NewGuid()), "tenant", "human", CancellationToken.None));
        Assert.Equal("review-stale", blocked.Code);
        Assert.Equal(1, harness.Publishes);
    }

    [UnitTest]
    public async Task UnknownExecutionOutcome_IsPersistedAndNeverBlindlyReplayed()
    {
        var harness = new Harness { FailPublication = true };
        var plan = await harness.Service.PlanAsync(harness.Spec, "tenant", "agent", CancellationToken.None);
        var command = new GaSpecApplyRequest(plan.SpecReview!.Revision, Guid.NewGuid());
        var result = await harness.Service.ApplyAsync(plan.OperationId, command, "tenant", "human", CancellationToken.None);
        Assert.Equal(WorkflowOperationStatus.ManualInterventionRequired, result.Status);
        Assert.DoesNotContain("CANARY", string.Join(',', result.BlockingReasons));
        await harness.Service.ApplyAsync(plan.OperationId, command, "tenant", "human", CancellationToken.None);
        Assert.Equal(1, harness.Publishes);
        Assert.Null(await harness.Service.GetAsync(plan.OperationId, "other-tenant", CancellationToken.None));
    }

    [UnitTest]
    public async Task ChangedPolicyAndProhibitedSecrets_FailClosed()
    {
        var harness = new Harness();
        var plan = await harness.Service.PlanAsync(harness.Spec, "tenant", "agent", CancellationToken.None);
        harness.Connection.UpdatedAt = harness.Connection.UpdatedAt.AddSeconds(1);
        var stale = await Assert.ThrowsAsync<GaSpecBlockedException>(() => harness.Service.ApplyAsync(plan.OperationId,
            new(plan.SpecReview!.Revision, Guid.NewGuid()), "tenant", "human", CancellationToken.None));
        Assert.Equal("review-stale", stale.Code);
        harness.Connection.SecretRef = "CANARY";
        harness.Connection.SecretType = "env";
        var secret = await Assert.ThrowsAsync<GaSpecBlockedException>(() => harness.Service.PlanAsync(harness.Spec,
            "tenant", "agent", CancellationToken.None));
        Assert.Equal("secret-provider-prohibited", secret.Code);
        Assert.Equal(0, harness.Publishes);
    }

    [UnitTest]
    public async Task CanonicalWorkspace_MissingOrChangedAuthority_FailsClosed()
    {
        var harness = new Harness();
        var unavailable = new GaSpecReviewService(harness.Store, harness.Adapter, new UnavailableGaSpecContextReader());
        var missing = await Assert.ThrowsAsync<GaSpecBlockedException>(() => unavailable.PlanAsync(harness.Spec,
            "tenant", "agent", CancellationToken.None));
        Assert.Equal("workspace-context-unavailable", missing.Code);
        var plan = await harness.Service.PlanAsync(harness.Spec, "tenant", "agent", CancellationToken.None);
        harness.ContextHash = "changed-canonical-snapshot";
        var stale = await Assert.ThrowsAsync<GaSpecBlockedException>(() => harness.Service.ApplyAsync(plan.OperationId,
            new(plan.SpecReview!.Revision, Guid.NewGuid()), "tenant", "human", CancellationToken.None));
        Assert.Equal("workspace-context-stale", stale.Code);
        Assert.Equal(0, harness.Publishes);
    }

    private sealed class Harness
    {
        internal readonly IWorkflowOperationStore Store = Substitute.For<IWorkflowOperationStore>();
        internal readonly GaLayerPublicationAdapter Adapter;
        internal readonly GaSpecReviewService Service;
        internal readonly IGaSpecContextReader Contexts = Substitute.For<IGaSpecContextReader>();
        internal readonly SpecLayerIntent Spec;
        internal readonly DataConnection Connection;
        internal PublishedLayerSummary? Layer;
        internal int Publishes;
        internal bool FailPublication;
        internal string ContextHash = "canonical-authorized-snapshot";
        private readonly ConcurrentDictionary<string, WorkflowOperationRecord> _records = new();
        private readonly object _sync = new();

        internal Harness()
        {
            var id = Guid.NewGuid();
            Spec = new() { BundleVersion = WorkflowBundle.LoadDefault().BundleVersion, ConnectionId = id, ContextRevision = "revision-1", Schema = "public", Table = "roads", LayerName = "Roads", ServiceName = "roads" };
            Connection = new() { ConnectionId = id, Host = "127.0.0.1", Port = 5432, DatabaseName = "gis", EncryptedConnectionString = [1] };
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"GaSpec:ConnectionBindings:{id:D}:TenantId"] = "tenant",
                [$"GaSpec:ConnectionBindings:{id:D}:Purpose"] = "layer.publish",
                [$"GaSpec:ConnectionBindings:{id:D}:Revision"] = "policy-1",
                [$"GaSpec:ConnectionBindings:{id:D}:Host"] = "127.0.0.1",
                [$"GaSpec:ConnectionBindings:{id:D}:Port"] = "5432",
                [$"GaSpec:ConnectionBindings:{id:D}:Database"] = "gis"
            }).Build();
            var registry = Substitute.For<ISecureConnectionRegistry>();
            registry.GetConnectionAsync(id, Arg.Any<CancellationToken>()).Returns(_ => Connection);
            var resolver = Substitute.For<ISecureConnectionResolver>();
            resolver.ResolveConnectionStringAsync(id, Arg.Any<CancellationToken>()).Returns("Host=127.0.0.1;Port=5432;Database=gis;Username=test;Password=CANARY");
            var publisher = Substitute.For<ILayerPublishingService>();
            publisher.ListPublishedLayersAsync(Arg.Any<string>(), Arg.Any<string>(), id, Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult<IReadOnlyList<PublishedLayerSummary>>(Layer is null ? [] : [Layer]));
            publisher.CountSourceRowsAsync(Arg.Any<string>(), "public", "roads", Arg.Any<CancellationToken>()).Returns(10L);
            publisher.PublishLayerAsync(Arg.Any<string>(), Arg.Any<LayerPublishRequest>(), Arg.Any<CancellationToken>()).Returns(_ =>
            {
                Interlocked.Increment(ref Publishes);
                if (FailPublication) throw new InvalidOperationException("provider Password=CANARY");
                Layer = new() { LayerId = 7, LayerName = "Roads", Schema = "public", Table = "roads", GeometryType = "LineString", Srid = 4326, Enabled = true, ServiceName = "roads", MetadataRevision = 1 };
                return Task.FromResult(Layer!);
            });
            Store.TryCreateAsync(Arg.Any<WorkflowOperationRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
                .Returns(call => _records.TryAdd(call.Arg<WorkflowOperationRecord>().OperationId, call.Arg<WorkflowOperationRecord>() with { Version = 1 }));
            Store.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
                _records.TryGetValue(call.Arg<string>(), out var record) ? record : null);
            Store.TrySetAsync(Arg.Any<WorkflowOperationRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                var record = call.Arg<WorkflowOperationRecord>();
                lock (_sync)
                {
                    if (!_records.TryGetValue(record.OperationId, out var old) || old.Version != record.Version) return false;
                    _records[record.OperationId] = record with { Version = record.Version + 1 };
                    return true;
                }
            });
            Adapter = new(registry, resolver, publisher, config);
            Contexts.ReadAuthorizedContextHashAsync(Arg.Any<SpecLayerIntent>(), "tenant", Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<bool>()).Returns(_ => ContextHash);
            Service = new(Store, Adapter, Contexts);
        }
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.AuditLog;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Plugins.Abstractions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Plugins.Tests;

public sealed class PluginFeatureEditHooksTests
{
    private static readonly FeatureEditHookScope Scope = new("parcels", 3) { LayerName = "Parcels", UserName = "alice", TraceId = "trace-1" };

    [UnitTest]
    public async Task RunBefore_MapsEveryRow_AndRejectionsBackToTheirBatchRows()
    {
        var pipeline = new RecordingPipeline
        {
            Before = context => Reject(context, (EditKind.Create, 1), (EditKind.Update, 0)),
        };
        var batch = FeatureEditBatch.Create(
            creates: [Row(0, "a"), Row(0, "b")],
            updates: [Row(11, "c")],
            deletes: [12]);

        var rejections = await Hooks(pipeline).RunBeforeAsync(Scope, batch, CancellationToken.None);

        var context = pipeline.BeforeContexts.Should().ContainSingle().Subject;
        context.ServiceId.Should().Be("parcels");
        context.LayerId.Should().Be(3);
        context.ResourceName.Should().Be("Parcels");
        context.Actor.Should().Be("alice");
        context.CorrelationId.Should().Be("trace-1");
        context.Features.Select(static feature => (feature.Kind, feature.RequestIndex, feature.ObjectId))
            .Should().Equal(
                (EditKind.Create, 0, (long?)null),
                (EditKind.Create, 1, (long?)null),
                (EditKind.Update, 0, (long?)11),
                (EditKind.Delete, 0, (long?)12));
        rejections.Should().Equal(
            new FeatureEditHookRejection(FeatureEditOperationKind.Create, 1, "rejected"),
            new FeatureEditHookRejection(FeatureEditOperationKind.Update, 0, "rejected"));
    }

    [UnitTest]
    public async Task RunBefore_OrderedOperations_CountsRowsPerKind()
    {
        var pipeline = new RecordingPipeline { Before = context => Reject(context, (EditKind.Create, 1)) };
        var batch = FeatureEditBatch.Create(operations:
        [
            FeatureEditOperation.Update(Row(11, "u")),
            FeatureEditOperation.Create(Row(0, "first")),
            FeatureEditOperation.Delete(5),
            FeatureEditOperation.Create(Row(0, "second")),
        ]);

        var rejections = await Hooks(pipeline).RunBeforeAsync(Scope, batch, CancellationToken.None);

        rejections.Should().ContainSingle()
            .Which.Should().Be(new FeatureEditHookRejection(FeatureEditOperationKind.Create, 1, "rejected"));
    }

    [UnitTest]
    public async Task RunBefore_RejectionForARowOutsideTheBatch_FailsInsteadOfLettingTheRowThrough()
    {
        var pipeline = new RecordingPipeline
        {
            Before = static _ => new PluginEditOutcome(
                [new PluginEditRejection(EditKind.Update, 4, 11, 1000, "rejected")],
                BatchRejected: false),
        };
        var batch = FeatureEditBatch.Create(updates: [Row(11, "c")]);

        var act = () => Hooks(pipeline).RunBeforeAsync(Scope, batch, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [UnitTest]
    public async Task RunBefore_Delete_SeesTheStoredRowWhenTheProtocolReadIt()
    {
        var pipeline = new RecordingPipeline();
        var scope = Scope with { StoredRows = new Dictionary<long, Feature> { [12] = Row(12, "stored") } };

        await Hooks(pipeline).RunBeforeAsync(scope, FeatureEditBatch.Create(deletes: [12, 13]), CancellationToken.None);

        var features = pipeline.BeforeContexts.Should().ContainSingle().Subject.Features;
        features[0].Feature.Attributes["name"].Should().Be("stored");
        features[1].Feature.Attributes.Should().BeEmpty();
        features[1].ObjectId.Should().Be(13);
    }

    [UnitTest]
    public async Task RunBefore_WithoutPlugins_DoesNotCallThePipeline()
    {
        var pipeline = new RecordingPipeline { HasPlugins = false };
        var hooks = Hooks(pipeline);

        var rejections = await hooks.RunBeforeAsync(Scope, FeatureEditBatch.Create(creates: [Row(0, "a")]), CancellationToken.None);

        hooks.HasHooks.Should().BeFalse();
        rejections.Should().BeEmpty();
        pipeline.BeforeContexts.Should().BeEmpty();
    }

    [UnitTest]
    public async Task RunAfter_SeesOnlyCommittedRows_WithTheirStoredIds()
    {
        var pipeline = new RecordingPipeline();
        var batch = FeatureEditBatch.Create(creates: [Row(0, "kept"), Row(0, "failed")], deletes: [12]);
        var result = FeatureEditResult.Success(
            createdCount: 1,
            updatedCount: 0,
            deletedCount: 1,
            createResults: [EditOperationResult.Success(101), EditOperationResult.Failure("write failed")],
            deleteResults: [EditOperationResult.Success(12)]);

        await Hooks(pipeline).RunAfterAsync(Scope, new FeatureEditOutcome(batch, result), CancellationToken.None);

        var features = pipeline.AfterContexts.Should().ContainSingle().Subject.Features;
        features.Select(static feature => (feature.Kind, feature.ObjectId, feature.Feature.Id))
            .Should().Equal((EditKind.Create, (long?)101, 101L), (EditKind.Delete, (long?)12, 12L));
        features[0].Feature.Attributes["name"].Should().Be("kept");
    }

    [UnitTest]
    public async Task RunAfter_RolledBackResult_RunsNoHook()
    {
        var pipeline = new RecordingPipeline();
        var batch = FeatureEditBatch.Create(creates: [Row(0, "a")], rollbackOnFailure: true);

        await Hooks(pipeline).RunAfterAsync(
            Scope,
            new FeatureEditOutcome(batch, FeatureEditResult.Rollback(createResults: [EditOperationResult.Success(5)])),
            CancellationToken.None);

        pipeline.AfterContexts.Should().BeEmpty();
    }

    [UnitTest]
    public async Task RunAfter_PipelineFailure_IsSwallowed()
    {
        var pipeline = new RecordingPipeline { AfterFailure = new InvalidOperationException("audit store down") };
        var batch = FeatureEditBatch.Create(deletes: [12]);
        var result = FeatureEditResult.Success(0, 0, 1, deleteResults: [EditOperationResult.Success(12)]);

        var act = () => Hooks(pipeline).RunAfterAsync(Scope, new FeatureEditOutcome(batch, result), CancellationToken.None);

        await act.Should().NotThrowAsync();
        pipeline.AfterContexts.Should().ContainSingle();
    }

    [UnitTest]
    public void AddHonuaPlugins_ReplacesTheNoOpHooksAnEditProtocolRegistered()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ILicenseEntitlementService>(new TestLicenseEntitlementService(HonuaEdition.Enterprise));
        services.AddSingleton<IAuditLog, NullAuditLog>();
        services.AddSingleton<IFeatureEditHooks>(NoOpFeatureEditHooks.Instance);

        services.AddHonuaPlugins(new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IFeatureEditHooks>().Should().BeOfType<PluginFeatureEditHooks>();
        services.Count(static descriptor => descriptor.ServiceType == typeof(IFeatureEditHooks)).Should().Be(1);
    }

    private static PluginFeatureEditHooks Hooks(IPluginEditPipeline pipeline)
        => new(pipeline, NullLogger<PluginFeatureEditHooks>.Instance);

    private static Feature Row(long id, string name)
        => Feature.Create(id, geometry: null, ImmutableDictionary<string, object?>.Empty.Add("name", name));

    private static PluginEditOutcome Reject(EditHookContext context, params (EditKind Kind, int Index)[] rows)
        =>
        new(
            [.. context.Features
                .Where(feature => rows.Contains((feature.Kind, feature.RequestIndex)))
                .Select(static feature => new PluginEditRejection(feature.Kind, feature.RequestIndex, feature.ObjectId, 1000, "rejected"))],
            BatchRejected: false);

    private sealed class RecordingPipeline : IPluginEditPipeline
    {
        public bool HasPlugins { get; init; } = true;

        public Func<EditHookContext, PluginEditOutcome> Before { get; init; } = static _ => PluginEditOutcome.Allowed;

        public Exception? AfterFailure { get; init; }

        public List<EditHookContext> BeforeContexts { get; } = [];

        public List<EditHookContext> AfterContexts { get; } = [];

        public ValueTask<PluginEditOutcome> ValidateAndRunBeforeHooksAsync(EditHookContext context, CancellationToken cancellationToken)
        {
            BeforeContexts.Add(context);
            return ValueTask.FromResult(Before(context));
        }

        public ValueTask RunAfterHooksAsync(EditHookContext context, CancellationToken cancellationToken)
        {
            AfterContexts.Add(context);
            return AfterFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(AfterFailure);
        }
    }
}

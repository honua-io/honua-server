// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.ReadOnlyProviders;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Query;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Events;
using Honua.Infrastructure.Validation;
using Honua.Protocols.Ogc.Api.Features;
using Honua.Protocols.Ogc.Api.Features.Services;
using Honua.Protocols.Ogc.Common;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Protocols.OgcApi.UnitTests.Features;

// OGC API Features item and batch edits run the contingent-value check and the plugin edit hooks.
public sealed class OgcFeaturesEditGateTests
{
    [UnitTest]
    public async Task ContingentViolation_OnCreateReplaceAndMergedPatch_IsRejectedBeforeTheWrite()
    {
        foreach (var request in new[] { Create("red", "L"), Replace(42, "red", "L"), Patch(42, "red", "L") })
        {
            foreach (var executed in await ExecuteBothAsync(request, new RecordingHooks()))
            {
                executed.Writer.ApplyCount.Should().Be(0);
                executed.Result.WasRolledBack.Should().BeTrue();
                var row = request.Operation == OgcFeaturesEditOperation.Create
                    ? executed.Result.CreateResults.Should().ContainSingle().Subject
                    : executed.Result.UpdateResults.Should().ContainSingle().Subject;
                row.ErrorMessage.Should().Contain("contingent-value group 'colorSize'");
                OgcFeatureEditFailureMapper.DetermineStatus(row).Should().Be(StatusCodes.Status400BadRequest);
            }
        }
    }

    [UnitTest]
    public async Task ValidCombination_IsWrittenAndAfterHooksSeeTheCommittedRow()
    {
        var hooks = new RecordingHooks();

        foreach (var executed in await ExecuteBothAsync(Create("red", "S"), hooks))
        {
            executed.Result.IsSuccess.Should().BeTrue();
            executed.Writer.ApplyCount.Should().Be(1);
        }

        hooks.BeforeBatches.Should().HaveCount(2);
        hooks.AfterCalls.Should().HaveCount(2);
        foreach (var (batch, result) in hooks.AfterCalls)
        {
            var row = FeatureEditRows.Enumerate(batch).Should().ContainSingle().Subject;
            FeatureEditRows.IsCommitted(result, row).Should().BeTrue();
            FeatureEditRows.ResultOf(result, row)!.Value.ObjectId.Should().Be(101);
        }
    }

    [UnitTest]
    public async Task PluginRejection_BlocksTheWrite_AndRunsNoAfterHook()
    {
        var hooks = new RecordingHooks { Reject = FeatureEditOperationKind.Update };

        foreach (var executed in await ExecuteBothAsync(Replace(42, "red", "S"), hooks))
        {
            executed.Writer.ApplyCount.Should().Be(0);
            executed.Result.WasRolledBack.Should().BeTrue();
            executed.Result.UpdateResults.Should().ContainSingle().Which.ErrorMessage.Should().Be("rejected by plugin");
        }

        hooks.AfterCalls.Should().BeEmpty();
    }

    [UnitTest]
    public async Task RejectedDelete_IsAClientError_AndTheHookSeesThePublishedLayerAndTheStoredRow()
    {
        var hooks = new RecordingHooks { Reject = FeatureEditOperationKind.Delete };
        var writer = new CountingWriter();
        var request = new OgcFeaturesEditRequest
        {
            Operation = OgcFeaturesEditOperation.Delete,
            ObjectId = 42,
            ServiceName = "parks",
            PublicLayerId = 3,
            StoredFeature = Row(42, "red", "S"),
        };

        var result = await CreateHandlers(writer).Crud.ExecuteEditAsync(Context(hooks), 7, Resource(), request, CancellationToken.None);

        writer.ApplyCount.Should().Be(0);
        var row = result.DeleteResults.Should().ContainSingle().Subject;
        OgcFeaturesCrudHandler.MapDeleteFailure(new DefaultHttpContext(), row, "42")
            .Should().BeAssignableTo<IStatusCodeHttpResult>().Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        var scope = hooks.Scopes.Should().ContainSingle().Subject;
        scope.ServiceId.Should().Be("parks");
        scope.LayerId.Should().Be(3, "plugins are scoped by the published layer, not the storage layer");
        scope.StoredRows.Should().ContainKey(42);
    }

    [UnitTest]
    public async Task BatchDelete_ShowsTheHookTheStoredRow()
    {
        var hooks = new RecordingHooks { Reject = FeatureEditOperationKind.Delete };
        var writer = new CountingWriter();
        var request = new OgcFeaturesEditRequest
        {
            Operation = OgcFeaturesEditOperation.Batch,
            RollbackOnFailure = true,
            BatchOperations =
            [
                new OgcFeaturesBatchEditOperation
                {
                    Operation = OgcFeaturesEditOperation.Delete,
                    ObjectId = 42,
                    StoredFeature = Row(42, "red", "S"),
                },
            ],
        };

        var result = await CreateHandlers(writer).Transaction.ExecuteEditAsync(Context(hooks), 7, Resource(), request, CancellationToken.None);

        writer.ApplyCount.Should().Be(0);
        hooks.Scopes.Should().ContainSingle().Which.StoredRows.Should().ContainKey(42);
        var row = result.DeleteResults.Should().ContainSingle().Subject;
        OgcFeatureEditFailureMapper.DetermineStatus(row).Should().Be(StatusCodes.Status400BadRequest);
    }

    [UnitTest]
    public async Task BatchWithOneInvalidRow_FailsTheWholeBatch()
    {
        var request = new OgcFeaturesEditRequest
        {
            Operation = OgcFeaturesEditOperation.Batch,
            RollbackOnFailure = true,
            BatchOperations =
            [
                new OgcFeaturesBatchEditOperation { Operation = OgcFeaturesEditOperation.Create, Feature = Row(0, "red", "S") },
                new OgcFeaturesBatchEditOperation { Operation = OgcFeaturesEditOperation.Create, Feature = Row(0, "red", "L") },
            ],
        };
        var hooks = new RecordingHooks();
        var writer = new CountingWriter();

        var result = await CreateHandlers(writer).Transaction.ExecuteEditAsync(Context(hooks), 7, Resource(), request, CancellationToken.None);

        writer.ApplyCount.Should().Be(0);
        hooks.BeforeBatches.Should().BeEmpty("a rollback batch stops at the first failing check");
        result.CreateResults.Select(static row => row.ErrorMessage).Should().Equal(
            "Operation rolled back.",
            "Attribute combination for fields (color, size) violates contingent-value group 'colorSize'.");
    }

    private static async Task<Executed[]> ExecuteBothAsync(OgcFeaturesEditRequest request, RecordingHooks hooks)
    {
        var executed = new Executed[2];
        for (var i = 0; i < executed.Length; i++)
        {
            var writer = new CountingWriter();
            var (crud, transaction) = CreateHandlers(writer);
            var result = i == 0
                ? await crud.ExecuteEditAsync(Context(hooks), 7, Resource(), request, CancellationToken.None)
                : await transaction.ExecuteEditAsync(Context(hooks), 7, Resource(), request, CancellationToken.None);
            executed[i] = new Executed(result, writer);
        }

        return executed;
    }

    private static OgcFeaturesEditRequest Create(string color, string size)
        => new() { Operation = OgcFeaturesEditOperation.Create, Feature = Row(0, color, size) };

    private static OgcFeaturesEditRequest Replace(long id, string color, string size)
        => new() { Operation = OgcFeaturesEditOperation.Replace, ObjectId = id, Feature = Row(id, color, size) };

    // The PATCH handler merges the stored row into the request before it reaches the edit pipeline.
    private static OgcFeaturesEditRequest Patch(long id, string color, string size)
        => new() { Operation = OgcFeaturesEditOperation.Patch, ObjectId = id, Feature = Row(id, color, size) };

    private static Feature Row(long id, string color, string size)
        => Feature.Create(id, null, ImmutableDictionary<string, object?>.Empty.Add("color", color).Add("size", size));

    private static (OgcFeaturesCrudHandler Crud, OgcFeaturesTransactionHandler Transaction) CreateHandlers(IFeatureWriter writer)
    {
        var reader = Substitute.For<IFeatureReader>();
        var geometry = new OgcFeaturesGeometryServices(
            Substitute.For<IGeometryService>(),
            Substitute.For<ICoordinateTransformService>(),
            Options.Create(new LimitsOptions()),
            NullLogger<OgcFeaturesGeometryServices>.Instance);
        var adapter = new OgcFeaturesEditParameterAdapter(NullLogger<OgcFeaturesEditParameterAdapter>.Instance);
        var processor = new EditProcessor(NullLogger<EditProcessor>.Instance);
        var validator = new FeatureMutationValidator(Substitute.For<IGeometryValidator>());
        var events = new FeatureMutationEventService(Substitute.For<IFeatureChangeEventPublisher>());
        var crud = new OgcFeaturesCrudHandler(
            new OgcFeaturesCrudDependencies(
                reader, writer, new WellKnownCrsRegistry(), geometry, adapter, processor,
                Substitute.For<IQueryProcessor>(), new ETagService(), validator, events),
            NullLogger<OgcFeaturesCrudHandler>.Instance);
        var transaction = new OgcFeaturesTransactionHandler(
            new OgcFeaturesTransactionDependencies(
                reader, writer, new WellKnownCrsRegistry(), geometry, adapter, processor,
                Substitute.For<IQueryProcessor>(), new ETagService(), validator, events),
            NullLogger<OgcFeaturesTransactionHandler>.Instance);
        return (crud, transaction);
    }

    private static MetadataV2Object Resource()
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "shirts", Name = "shirts" },
            Type = MetadataV2ObjectType.FeatureDataset,
            SchemaFields =
            [
                new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false, SemanticRoles = ["id.primary"] },
                new MetadataV2Field { Name = "color", Type = MetadataV2FieldType.String, Nullable = true, Editable = true },
                new MetadataV2Field { Name = "size", Type = MetadataV2FieldType.String, Nullable = true, Editable = true },
            ],
            ContingentValueGroups =
            [
                new MetadataV2ContingentValueGroup
                {
                    Name = "colorSize",
                    Restrictive = true,
                    Fields = ["color", "size"],
                    ContingentValues =
                    [
                        new MetadataV2ContingentValue
                        {
                            Id = 1,
                            Values = new Dictionary<string, MetadataV2ContingentFieldValue>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["color"] = new() { Type = "code", Code = JsonElement.Parse("\"red\"") },
                                ["size"] = new() { Type = "code", Code = JsonElement.Parse("\"S\"") },
                            },
                        },
                    ],
                },
            ],
        };

    private static DefaultHttpContext Context(IFeatureEditHooks hooks)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOptions<RbacOptions>>(Options.Create(new RbacOptions()));
        services.AddSingleton(hooks);
        return new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
    }

    private readonly record struct Executed(FeatureEditResult Result, CountingWriter Writer);

    private sealed class CountingWriter : IFeatureWriter
    {
        public int ApplyCount { get; private set; }

        public Task<FeatureEditResult> ApplyEditsAsync(int layerId, FeatureEditBatch editBatch, CancellationToken cancellationToken = default)
        {
            ApplyCount++;
            var rows = FeatureEditRows.Enumerate(editBatch);
            ImmutableArray<EditOperationResult> Results(FeatureEditOperationKind kind)
                => [.. rows.Where(row => row.Kind == kind).Select(row => EditOperationResult.Success(row.ObjectId ?? 101))];
            return Task.FromResult(FeatureEditResult.Success(
                rows.Count(static row => row.Kind == FeatureEditOperationKind.Create),
                rows.Count(static row => row.Kind == FeatureEditOperationKind.Update),
                rows.Count(static row => row.Kind == FeatureEditOperationKind.Delete),
                createResults: Results(FeatureEditOperationKind.Create),
                updateResults: Results(FeatureEditOperationKind.Update),
                deleteResults: Results(FeatureEditOperationKind.Delete)));
        }

        public Task<Feature> CreateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<Feature> UpdateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> DeleteAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class RecordingHooks : IFeatureEditHooks
    {
        public FeatureEditOperationKind? Reject { get; init; }

        public List<FeatureEditBatch> BeforeBatches { get; } = [];

        public List<FeatureEditHookScope> Scopes { get; } = [];

        public List<(FeatureEditBatch Batch, FeatureEditResult Result)> AfterCalls { get; } = [];

        public bool HasHooks => true;

        public Task<IReadOnlyList<FeatureEditHookRejection>> RunBeforeAsync(
            FeatureEditHookScope scope,
            FeatureEditBatch batch,
            CancellationToken cancellationToken)
        {
            BeforeBatches.Add(batch);
            Scopes.Add(scope);
            IReadOnlyList<FeatureEditHookRejection> rejections = Reject is { } kind
                ? [new FeatureEditHookRejection(kind, 0, "rejected by plugin")]
                : [];
            return Task.FromResult(rejections);
        }

        public Task RunAfterAsync(FeatureEditHookScope scope, FeatureEditOutcome outcome, CancellationToken cancellationToken)
        {
            var (batch, result) = outcome;
            AfterCalls.Add((batch, result));
            return Task.CompletedTask;
        }
    }
}

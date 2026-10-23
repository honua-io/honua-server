// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Query;
using Honua.Core.Queries.Filters;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Events;
using Honua.Infrastructure.Validation;
using Honua.Protocols.Ogc.Classic.Wfs20;
using Honua.Protocols.Ogc.Common;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Protocols.OgcClassic.UnitTests.Wfs20;

/// <summary>
/// WFS Transaction applies the shared owner-edit policy before the write commits.
/// </summary>
public sealed class Wfs20OwnerEditPolicyTests
{
    private const string NonOwnerDenial =
        "Feature is owned by another principal and cannot be modified under the owner-based edit policy.";
    private const string AnonymousDenial =
        "Anonymous edits are not permitted while an owner-based edit policy is active.";

    private static readonly MetadataV2OwnerEditPolicy Policy = new()
    {
        Enabled = true,
        OwnerField = "owner",
        StampOwnerOnInsert = true
    };

    [UnitTest]
    public async Task ExecutePreparedEditAsync_NonOwnerUpdateAndDelete_AreDeniedBeforeTheWrite()
    {
        var updated = await ExecuteAsync(Principal("bob", admin: false), Policy, Update(42));
        var deleted = await ExecuteAsync(Principal("bob", admin: false), Policy, Delete(42));

        updated.Result.WasRolledBack.Should().BeTrue();
        deleted.Result.WasRolledBack.Should().BeTrue();
        updated.Result.UpdateResults.Should().ContainSingle();
        deleted.Result.DeleteResults.Should().ContainSingle();
        updated.Result.UpdateResults[0].ErrorCode.Should().Be(EditOperationResult.NotPermittedErrorCode);
        deleted.Result.DeleteResults[0].ErrorCode.Should().Be(EditOperationResult.NotPermittedErrorCode);
        updated.Result.UpdateResults[0].ErrorMessage.Should().Be(NonOwnerDenial);
        deleted.Result.DeleteResults[0].ErrorMessage.Should().Be(NonOwnerDenial);
        updated.Result.UpdateResults[0].ErrorMessage.Should().NotContain("alice");
        updated.Writer.ApplyCount.Should().Be(0);
        deleted.Writer.ApplyCount.Should().Be(0);
        updated.Reader.QueryCount.Should().Be(1);
        deleted.Reader.QueryCount.Should().Be(1);
    }

    [UnitTest]
    public async Task ExecutePreparedEditAsync_Owner_SucceedsAndPreservesTheStoredOwner()
    {
        var executed = await ExecuteAsync(Principal("alice", admin: false), Policy, Update(42));

        executed.Result.IsSuccess.Should().BeTrue();
        executed.Writer.ApplyCount.Should().Be(1);
        executed.Writer.LastBatch.Operations.Should().ContainSingle();
        executed.Writer.LastBatch.Operations[0].Feature!.Value.Attributes["owner"].Should().Be("alice");
        executed.Writer.LastBatch.Operations[0].Feature!.Value.Attributes["name"].Should().Be("visible");
    }

    [UnitTest]
    public async Task ExecutePreparedEditAsync_Admin_BypassesWithoutReadingTheOwner()
    {
        var executed = await ExecuteAsync(Principal("carol", admin: true), Policy, Update(42));

        executed.Result.IsSuccess.Should().BeTrue();
        executed.Reader.QueryCount.Should().Be(0);
        executed.Writer.ApplyCount.Should().Be(1);
        executed.Writer.LastBatch.Operations[0].Feature!.Value.Attributes.ContainsKey("owner").Should().BeFalse();
    }

    [UnitTest]
    public async Task ExecutePreparedEditAsync_Anonymous_IsDeniedWithoutReading()
    {
        var executed = await ExecuteAsync(new DefaultHttpContext(), Policy, Insert());

        executed.Result.WasRolledBack.Should().BeTrue();
        executed.Result.CreateResults.Should().ContainSingle();
        executed.Result.CreateResults[0].ErrorMessage.Should().Be(AnonymousDenial);
        executed.Writer.ApplyCount.Should().Be(0);
        executed.Reader.QueryCount.Should().Be(0);
    }

    [UnitTest]
    public async Task ExecutePreparedEditAsync_Insert_StampsOnlyWhenRequested()
    {
        var stamped = await ExecuteAsync(Principal("alice", admin: false), Policy, Insert());
        var unstamped = await ExecuteAsync(
            Principal("alice", admin: false),
            Policy with { StampOwnerOnInsert = false },
            Insert());
        var disabled = await ExecuteAsync(Principal("bob", admin: false), Policy with { Enabled = false }, Update(42));
        var absent = await ExecuteAsync(Principal("bob", admin: false), policy: null, Update(42));

        stamped.Result.IsSuccess.Should().BeTrue();
        stamped.Reader.QueryCount.Should().Be(0);
        stamped.Writer.LastBatch.Operations[0].Feature!.Value.Attributes["owner"].Should().Be("alice");
        unstamped.Result.IsSuccess.Should().BeTrue();
        unstamped.Writer.LastBatch.Operations[0].Feature!.Value.Attributes.ContainsKey("owner").Should().BeFalse();
        disabled.Result.IsSuccess.Should().BeTrue();
        absent.Result.IsSuccess.Should().BeTrue();
        disabled.Reader.QueryCount.Should().Be(0);
        absent.Reader.QueryCount.Should().Be(0);
        disabled.Writer.ApplyCount.Should().Be(1);
        absent.Writer.ApplyCount.Should().Be(1);
    }

    private static Wfs20EditOperation Update(long id)
        => new(FeatureEditOperation.Update(Feature.Create(
            id,
            null,
            ImmutableDictionary<string, object?>.Empty.Add("name", "visible"))));

    private static Wfs20EditOperation Delete(long id)
        => new(FeatureEditOperation.Delete(id));

    private static Wfs20EditOperation Insert()
        => new(FeatureEditOperation.Create(Feature.Create(
            0,
            null,
            ImmutableDictionary<string, object?>.Empty.Add("name", "created"))));

    private static async Task<(FeatureEditResult Result, CapturingWriter Writer, OwnerReader Reader)> ExecuteAsync(
        HttpContext context,
        MetadataV2OwnerEditPolicy? policy,
        Wfs20EditOperation operation)
    {
        var reader = new OwnerReader();
        var writer = new CapturingWriter();
        var handler = CreateHandler(reader, writer);
        var result = await handler.ExecutePreparedEditAsync(
            context,
            layerId: 7,
            resource: Resource(policy),
            operations: ImmutableArray.Create(operation),
            requestGeometryChangedFlags: ImmutableArray.Create(false),
            rollbackOnFailure: true,
            CancellationToken.None);
        return (result, writer, reader);
    }

    private static MetadataV2Resource Resource(MetadataV2OwnerEditPolicy? policy)
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "cities", Name = "cities" },
            Type = MetadataV2ResourceType.FeatureDataset,
            OwnerEditPolicy = policy,
            SchemaFields =
            [
                new MetadataV2Field
                {
                    Name = "objectid",
                    Type = MetadataV2FieldType.Integer,
                    Nullable = false,
                    SemanticRoles = ["id.primary"]
                },
                new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String, Nullable = true, Editable = true }
            ]
        };

    private static DefaultHttpContext Principal(string subject, bool admin)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOptions<RbacOptions>>(Options.Create(new RbacOptions()));
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, subject),
            new(ClaimTypes.Role, admin ? "admin" : "editor")
        };
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        return context;
    }

    private static Wfs20Handler CreateHandler(IFeatureReader reader, IFeatureWriter writer)
    {
        var coordinateTransform = Substitute.For<ICoordinateTransformService>();
        var queryServices = new Wfs20QueryServices(
            reader,
            Substitute.For<IGmlFeatureStore>(),
            Substitute.For<IMetadataV2GraphProvider>(),
            Substitute.For<IFilterExpressionService>(),
            new Wfs20QueryParameterAdapter(NullLogger<Wfs20QueryParameterAdapter>.Instance),
            Substitute.For<IQueryProcessor>(),
            Options.Create(new Wfs20Options()),
            Substitute.For<IConfiguration>());
        var editServices = new Wfs20EditServices(
            writer,
            new Wfs20EditParameterAdapter(NullLogger<Wfs20EditParameterAdapter>.Instance),
            new EditProcessor(NullLogger<EditProcessor>.Instance),
            new FeatureMutationValidator(Substitute.For<IGeometryValidator>()),
            new FeatureMutationEventService(Substitute.For<IFeatureChangeEventPublisher>()),
            Options.Create(new LimitsOptions()));
        var spatialServices = new Wfs20SpatialServices(
            new OgcFeaturesGeometryServices(
                Substitute.For<IGeometryService>(),
                coordinateTransform,
                Options.Create(new LimitsOptions()),
                NullLogger<OgcFeaturesGeometryServices>.Instance),
            coordinateTransform,
            Substitute.For<ICrsRegistry>());
        return new Wfs20Handler(NullLogger<Wfs20Handler>.Instance, queryServices, editServices, spatialServices);
    }

    private sealed class CapturingWriter : IFeatureWriter
    {
        public int ApplyCount { get; private set; }

        public FeatureEditBatch LastBatch { get; private set; }

        public Task<FeatureEditResult> ApplyEditsAsync(
            int layerId,
            FeatureEditBatch editBatch,
            CancellationToken cancellationToken = default)
        {
            ApplyCount++;
            LastBatch = editBatch;
            var creates = 0;
            var updates = 0;
            var deletes = 0;
            if (!editBatch.Operations.IsDefaultOrEmpty)
            {
                foreach (var operation in editBatch.Operations)
                {
                    switch (operation.Kind)
                    {
                        case FeatureEditOperationKind.Create:
                            creates++;
                            break;
                        case FeatureEditOperationKind.Update:
                            updates++;
                            break;
                        case FeatureEditOperationKind.Delete:
                            deletes++;
                            break;
                    }
                }
            }

            return Task.FromResult(FeatureEditResult.Success(creates, updates, deletes));
        }

        public Task<Feature> CreateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<Feature> UpdateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> DeleteAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class OwnerReader : IFeatureReader
    {
        public int QueryCount { get; private set; }

        public Task<Feature?> GetAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<QueryResult<Feature>> QueryAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
        {
            QueryCount++;
            var features = ImmutableArray.CreateBuilder<Feature>();
            if (query.ObjectIds is { } ids)
            {
                foreach (var id in ids)
                {
                    features.Add(Feature.Create(
                        id,
                        null,
                        ImmutableDictionary.CreateRange(new Dictionary<string, object?> { ["owner"] = "alice" })));
                }
            }

            return Task.FromResult(QueryResult<Feature>.Create(features.Count, features.ToImmutable()));
        }

        public Task<byte[]?> QueryFlatGeobufAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ImmutableArray<long>> QueryObjectIdsAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<long> CountAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<FeatureExtent?> GetExtentAsync(int layerId, FeatureQuery? query = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryStatisticsAsync(
            int layerId,
            FeatureQuery query,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<TemporalExtentResult?> GetTemporalExtentAsync(
            int layerId,
            string fieldName,
            TemporalPropertyType propertyType,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<EstimateResult> GetEstimatesAsync(int layerId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<QueryResult<Feature>> QueryTopFeaturesAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryDateBinsAsync(
            int layerId,
            FeatureQuery query,
            DateBinDefinition dateBin,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryBinsAsync(
            int layerId,
            FeatureQuery query,
            BinDefinition binDefinition,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryH3Async(
            int layerId,
            FeatureQuery query,
            H3AggregationQuery h3Query,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}

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

/// <summary>
/// OGC API Features item and transaction edits share the owner-edit policy.
/// </summary>
public sealed class OgcFeaturesOwnerEditPolicyTests
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
    public async Task ExecuteEditAsync_NonOwnerUpdateAndDelete_AreDeniedBeforeTheWrite()
    {
        var updated = await ExecuteBothAsync(Principal("bob", admin: false), Policy, Patch(42));
        var deleted = await ExecuteBothAsync(Principal("bob", admin: false), Policy, Delete(42));

        foreach (var executed in updated.Concat(deleted))
        {
            executed.Result.WasRolledBack.Should().BeTrue();
            executed.Writer.ApplyCount.Should().Be(0);
            executed.Reader.QueryCount.Should().Be(1);
        }

        updated[0].Result.UpdateResults[0].ErrorCode.Should().Be(EditOperationResult.NotPermittedErrorCode);
        updated[0].Result.UpdateResults[0].ErrorMessage.Should().Be(NonOwnerDenial);
        updated[0].Result.UpdateResults[0].ErrorMessage.Should().NotContain("alice");
        updated[1].Result.UpdateResults[0].ErrorMessage.Should().Be(NonOwnerDenial);
        deleted[0].Result.DeleteResults[0].ErrorCode.Should().Be(EditOperationResult.NotPermittedErrorCode);
        deleted[0].Result.DeleteResults[0].ErrorMessage.Should().Be(NonOwnerDenial);
        deleted[1].Result.DeleteResults[0].ErrorMessage.Should().Be(NonOwnerDenial);
    }

    [UnitTest]
    public async Task ExecuteEditAsync_Owner_SucceedsAndPreservesTheStoredOwner()
    {
        var executed = await ExecuteBothAsync(Principal("alice", admin: false), Policy, Patch(42));

        foreach (var item in executed)
        {
            item.Result.IsSuccess.Should().BeTrue();
            item.Writer.ApplyCount.Should().Be(1);
            item.Writer.LastBatch.Updates.Should().ContainSingle();
            item.Writer.LastBatch.Updates[0].Attributes["owner"].Should().Be("alice");
            item.Writer.LastBatch.Updates[0].Attributes["name"].Should().Be("visible");
        }
    }

    [UnitTest]
    public async Task ExecuteEditAsync_Admin_BypassesWithoutReadingTheOwner()
    {
        var executed = await ExecuteBothAsync(Principal("carol", admin: true), Policy, Patch(42));

        foreach (var item in executed)
        {
            item.Result.IsSuccess.Should().BeTrue();
            item.Reader.QueryCount.Should().Be(0);
            item.Writer.ApplyCount.Should().Be(1);
            item.Writer.LastBatch.Updates[0].Attributes.ContainsKey("owner").Should().BeFalse();
        }
    }

    [UnitTest]
    public async Task ExecuteEditAsync_AnonymousAndDisabledPolicy_KeepTheEvaluatorOutcome()
    {
        var anonymous = await ExecuteBothAsync(new DefaultHttpContext(), Policy, Insert());
        var disabled = await ExecuteBothAsync(Principal("bob", admin: false), Policy with { Enabled = false }, Patch(42));
        var absent = await ExecuteBothAsync(Principal("bob", admin: false), policy: null, Patch(42));
        var stamped = await ExecuteBothAsync(Principal("alice", admin: false), Policy, Insert());

        foreach (var item in anonymous)
        {
            item.Result.WasRolledBack.Should().BeTrue();
            item.Result.CreateResults[0].ErrorMessage.Should().Be(AnonymousDenial);
            item.Writer.ApplyCount.Should().Be(0);
            item.Reader.QueryCount.Should().Be(0);
        }

        foreach (var item in disabled.Concat(absent))
        {
            item.Result.IsSuccess.Should().BeTrue();
            item.Writer.ApplyCount.Should().Be(1);
            item.Reader.QueryCount.Should().Be(0);
        }

        foreach (var item in stamped)
        {
            item.Result.IsSuccess.Should().BeTrue();
            item.Reader.QueryCount.Should().Be(0);
            item.Writer.LastBatch.Creates[0].Attributes["owner"].Should().Be("alice");
        }
    }

    [UnitTest]
    public async Task MapDeleteFailure_OwnerDenial_IsForbiddenAndOtherFailuresStayPut()
    {
        var forbidden = await ExecuteResultAsync(OgcFeaturesCrudHandler.MapDeleteFailure(
            Context(),
            EditOperationResult.Failure(NonOwnerDenial, EditOperationResult.NotPermittedErrorCode, 42),
            "42"));
        var missing = OgcFeaturesCrudHandler.MapDeleteFailure(
            Context(),
            EditOperationResult.Failure("Feature not found.", 1000, 42),
            "42");
        var failed = OgcFeaturesCrudHandler.MapDeleteFailure(
            Context(),
            EditOperationResult.Failure("database failed", 1000, 42),
            "42");
        var precondition = OgcFeaturesCrudHandler.MapDeleteFailure(
            Context(),
            EditOperationResult.PreconditionFailed(42),
            "42");

        forbidden.Status.Should().Be(StatusCodes.Status403Forbidden);
        forbidden.Body.Should().Contain(NonOwnerDenial);
        missing.Should().BeAssignableTo<IStatusCodeHttpResult>().Which.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        failed.Should().BeAssignableTo<IStatusCodeHttpResult>().Which.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        precondition.Should().BeAssignableTo<IStatusCodeHttpResult>().Which.StatusCode.Should().Be(StatusCodes.Status412PreconditionFailed);
    }

    private static OgcFeaturesEditRequest Patch(long id)
        => new()
        {
            Operation = OgcFeaturesEditOperation.Patch,
            ObjectId = id,
            Feature = Feature.Create(id, null, ImmutableDictionary<string, object?>.Empty.Add("name", "visible"))
        };

    private static OgcFeaturesEditRequest Delete(long id)
        => new() { Operation = OgcFeaturesEditOperation.Delete, ObjectId = id };

    private static OgcFeaturesEditRequest Insert()
        => new()
        {
            Operation = OgcFeaturesEditOperation.Create,
            Feature = Feature.Create(0, null, ImmutableDictionary<string, object?>.Empty.Add("name", "created"))
        };

    private static async Task<Executed[]> ExecuteBothAsync(
        HttpContext context,
        MetadataV2OwnerEditPolicy? policy,
        OgcFeaturesEditRequest request)
    {
        var resource = Resource(policy);
        var results = new Executed[2];
        for (var i = 0; i < 2; i++)
        {
            var reader = new OwnerReader();
            var writer = new CapturingWriter();
            var (crud, transaction) = CreateHandlers(reader, writer);
            var result = i == 0
                ? await crud.ExecuteEditAsync(context, 7, resource, request, CancellationToken.None)
                : await transaction.ExecuteEditAsync(context, 7, resource, request, CancellationToken.None);
            results[i] = new Executed(result, writer, reader);
        }

        return results;
    }

    private static (OgcFeaturesCrudHandler Crud, OgcFeaturesTransactionHandler Transaction) CreateHandlers(
        IFeatureReader reader,
        IFeatureWriter writer)
    {
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
                reader,
                writer,
                Substitute.For<ICrsRegistry>(),
                geometry,
                adapter,
                processor,
                Substitute.For<IQueryProcessor>(),
                Substitute.For<IETagService>(),
                validator,
                events),
            NullLogger<OgcFeaturesCrudHandler>.Instance);
        var transaction = new OgcFeaturesTransactionHandler(
            new OgcFeaturesTransactionDependencies(
                reader,
                writer,
                Substitute.For<ICrsRegistry>(),
                geometry,
                adapter,
                processor,
                Substitute.For<IQueryProcessor>(),
                Substitute.For<IETagService>(),
                validator,
                events),
            NullLogger<OgcFeaturesTransactionHandler>.Instance);
        return (crud, transaction);
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
        var context = Context();
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, subject),
            new(ClaimTypes.Role, admin ? "admin" : "editor")
        };
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        return context;
    }

    private static DefaultHttpContext Context()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IOptions<RbacOptions>>(Options.Create(new RbacOptions()));
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.Request.Path = "/ogc/features/collections/cities/items/42";
        return context;
    }

    private static async Task<(int Status, string Body)> ExecuteResultAsync(IResult result)
    {
        var context = Context();
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        return (context.Response.StatusCode, await reader.ReadToEndAsync());
    }

    private readonly record struct Executed(FeatureEditResult Result, CapturingWriter Writer, OwnerReader Reader);

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
            return Task.FromResult(FeatureEditResult.Success(
                editBatch.Creates.IsDefaultOrEmpty ? 0 : editBatch.Creates.Length,
                editBatch.Updates.IsDefaultOrEmpty ? 0 : editBatch.Updates.Length,
                editBatch.Deletes.IsDefaultOrEmpty ? 0 : editBatch.Deletes.Length));
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

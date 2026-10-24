// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Validation;
using Honua.Protocols.OData.Services;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Protocols.OData.UnitTests.Services;

/// <summary>
/// OData create, update, and delete go through the same owner-edit gate as FeatureServer.
/// </summary>
public sealed class ODataCrudOwnerEditPolicyTests
{
    private const int RouteLayerId = 1;
    private const int StorageLayerId = 7;
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
    public async Task UpdateAndDelete_NonOwner_AreDeniedBeforeTheWrite()
    {
        var reader = new OwnerReader();
        var writer = new CapturingWriter();
        var service = CreateService(reader, writer, Principal("bob", admin: false));

        var updated = await service.UpdateFeatureAsync(
            RouteLayerId, 42, Payload("After"), "https://example.test", null, null, StorageLayerId, false, Resource(Policy), CancellationToken.None);
        var deleted = await service.DeleteFeatureAsync(
            RouteLayerId, 42, null, null, StorageLayerId, Resource(Policy), CancellationToken.None);

        updated.IsSuccess.Should().BeFalse();
        deleted.IsSuccess.Should().BeFalse();
        updated.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        deleted.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        updated.ErrorMessage.Should().Be(NonOwnerDenial);
        deleted.ErrorMessage.Should().Be(NonOwnerDenial);
        updated.ErrorMessage.Should().NotContain("alice");
        writer.ApplyCount.Should().Be(0);
        reader.QueryCount.Should().Be(2);
    }

    [UnitTest]
    public async Task Update_Owner_SucceedsAndPreservesTheStoredOwner()
    {
        var reader = new OwnerReader();
        var writer = new CapturingWriter();
        var service = CreateService(reader, writer, Principal("alice", admin: false));

        var updated = await service.UpdateFeatureAsync(
            RouteLayerId, 42, Payload("After"), "https://example.test", null, null, StorageLayerId, false, Resource(Policy), CancellationToken.None);

        updated.IsSuccess.Should().BeTrue();
        updated.StatusCode.Should().Be(StatusCodes.Status200OK);
        writer.ApplyCount.Should().Be(1);
        writer.LastBatch.Updates.Should().ContainSingle();
        writer.LastBatch.Updates[0].Attributes["owner"].Should().Be("alice");
        writer.LastBatch.Updates[0].Attributes["name"].Should().Be("After");
    }

    [UnitTest]
    public async Task Update_Admin_BypassesWithoutReadingTheOwner()
    {
        var reader = new OwnerReader();
        var writer = new CapturingWriter();
        var service = CreateService(reader, writer, Principal("carol", admin: true));

        var updated = await service.UpdateFeatureAsync(
            RouteLayerId, 42, Payload("After"), "https://example.test", null, null, StorageLayerId, false, Resource(Policy), CancellationToken.None);

        updated.IsSuccess.Should().BeTrue();
        writer.ApplyCount.Should().Be(1);
        reader.QueryCount.Should().Be(0);
        writer.LastBatch.Updates[0].Attributes.ContainsKey("owner").Should().BeFalse();
    }

    [UnitTest]
    public async Task Create_Anonymous_IsDeniedWithoutReading()
    {
        var reader = new OwnerReader();
        var writer = new CapturingWriter();
        var service = CreateService(reader, writer, httpContext: null);

        var created = await service.CreateFeatureAsync(
            RouteLayerId, Payload("Created"), "https://example.test", StorageLayerId, Resource(Policy), CancellationToken.None);

        created.IsSuccess.Should().BeFalse();
        created.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        created.ErrorMessage.Should().Be(AnonymousDenial);
        writer.ApplyCount.Should().Be(0);
        reader.QueryCount.Should().Be(0);
    }

    [UnitTest]
    public async Task Create_StampsTheOwner_AndADisabledPolicyDoesNot()
    {
        var stampedReader = new OwnerReader();
        var stampedWriter = new CapturingWriter();
        var stamped = CreateService(stampedReader, stampedWriter, Principal("alice", admin: false));
        var created = await stamped.CreateFeatureAsync(
            RouteLayerId, Payload("Created"), "https://example.test", StorageLayerId, Resource(Policy), CancellationToken.None);

        created.IsSuccess.Should().BeTrue();
        created.StatusCode.Should().Be(StatusCodes.Status201Created);
        stampedWriter.ApplyCount.Should().Be(1);
        stampedReader.QueryCount.Should().Be(0);
        stampedWriter.LastBatch.Creates[0].Attributes["owner"].Should().Be("alice");

        var openReader = new OwnerReader();
        var openWriter = new CapturingWriter();
        var open = CreateService(openReader, openWriter, httpContext: null);
        var unstamped = await open.CreateFeatureAsync(
            RouteLayerId,
            Payload("Created"),
            "https://example.test",
            StorageLayerId,
            Resource(Policy with { Enabled = false }),
            CancellationToken.None);

        unstamped.IsSuccess.Should().BeTrue();
        openWriter.ApplyCount.Should().Be(1);
        openReader.QueryCount.Should().Be(0);
        openWriter.LastBatch.Creates[0].Attributes.ContainsKey("owner").Should().BeFalse();
    }

    private static ParsedFeaturePayload Payload(string name)
        => new() { Attributes = new Dictionary<string, object?> { ["name"] = name } };

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
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, subject) };
        if (admin)
        {
            claims.Add(new Claim(ClaimTypes.Role, "admin"));
        }

        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        return context;
    }

    private static ODataCrudService CreateService(OwnerReader reader, CapturingWriter writer, HttpContext? httpContext)
    {
        var dependencies = new ODataCrudDependencies(
            Substitute.For<IResourceValidator>(),
            reader,
            writer,
            Substitute.For<IGeometryService>(),
            new NullCrsRegistry(),
            new FixedETagService(),
            new FeatureMutationValidator(Substitute.For<IGeometryValidator>()),
            new ODataEditParameterAdapter(NullLogger<ODataEditParameterAdapter>.Instance),
            new EditProcessor(NullLogger<EditProcessor>.Instance),
            new FixedHttpContextAccessor(httpContext));
        return new ODataCrudService(dependencies, NullLogger<ODataCrudLog>.Instance);
    }

    private sealed class FixedHttpContextAccessor(HttpContext? context) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = context;
    }

    private sealed class NullCrsRegistry : ICrsRegistry
    {
        public ValueTask<CrsDefinition?> ResolveAsync(string? crsIdentifier, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<CrsDefinition?>(null);

        public ValueTask<CrsDefinition?> ResolveBySridAsync(int srid, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<CrsDefinition?>(null);

        public ValueTask<bool> IsSridSupportedAsync(int srid, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);
    }

    private sealed class FixedETagService : IETagService
    {
        public string ComputeETag<T>(T obj, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> jsonTypeInfo)
            => "\"etag\"";

        public string ComputeETag(ReadOnlySpan<byte> data) => "\"etag\"";

        public string ComputeETag(string content) => "\"etag\"";

        public bool IsModified(string? ifNoneMatch, string currentETag) => true;

        public bool MatchesPrecondition(string? ifMatch, string currentETag) => true;

        public void SetCacheHeaders(HttpResponse response, string etag, DateTimeOffset? lastModified = null)
        {
        }
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
            var created = editBatch.Creates.IsDefaultOrEmpty ? 0 : editBatch.Creates.Length;
            var updated = editBatch.Updates.IsDefaultOrEmpty ? 0 : editBatch.Updates.Length;
            var deleted = editBatch.Deletes.IsDefaultOrEmpty ? 0 : editBatch.Deletes.Length;
            return Task.FromResult(FeatureEditResult.Success(
                created,
                updated,
                deleted,
                createdIds: created > 0 ? ImmutableArray.Create(77L) : default,
                createResults: created > 0 ? ImmutableArray.Create(EditOperationResult.Success(77)) : default,
                updateResults: updated > 0 ? ImmutableArray.Create(EditOperationResult.Success(42)) : default,
                deleteResults: deleted > 0 ? ImmutableArray.Create(EditOperationResult.Success(42)) : default));
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
            => Task.FromResult<Feature?>(featureId == 42 ? Named(42) : null);

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

        private static Feature Named(long id)
            => Feature.Create(id, null, ImmutableDictionary<string, object?>.Empty.Add("name", "visible"));
    }
}

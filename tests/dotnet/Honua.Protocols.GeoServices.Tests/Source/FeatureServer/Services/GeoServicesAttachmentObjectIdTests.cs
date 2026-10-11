// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.Attachments.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;
using Honua.Protocols.GeoServices.FeatureServer;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

public sealed class GeoServicesAttachmentObjectIdTests
{
    [UnitTest]
    public async Task ResolveAsync_CustomObjectId_ReturnsStorageIdWithoutGetByPublicId()
    {
        var resource = CustomObjectIdLayer();
        var stored = Feature.Create(99, null, ImmutableDictionary<string, object?>.Empty.Add("gid", 7L));
        var reader = Substitute.For<IFeatureReader>();
        reader.GetAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Feature?>(new InvalidOperationException("public object id used as storage id")));
        reader.QueryAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(QueryResult<Feature>.Create(1, ImmutableArray.Create(stored))));
        var filters = SuccessFilters();

        var feature = await GeoServicesFeatureObjectIdResolver.ResolveAsync(
            reader, filters, resource, 3, 7, CancellationToken.None);

        feature.Should().NotBeNull();
        feature!.Value.Id.Should().Be(99);
    }

    [UnitTest]
    public async Task ResolveAsync_StorageObjectId_UsesGetAsync()
    {
        var stored = Feature.Create(7, null, ImmutableDictionary<string, object?>.Empty);
        var reader = Substitute.For<IFeatureReader>();
        reader.GetAsync(3, 7, Arg.Any<CancellationToken>()).Returns(Task.FromResult<Feature?>(stored));

        var feature = await GeoServicesFeatureObjectIdResolver.ResolveAsync(
            reader, SuccessFilters(), StorageObjectIdLayer(), 3, 7, CancellationToken.None);

        feature!.Value.Id.Should().Be(7);
        await reader.DidNotReceive().QueryAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task ResolveStorageIdsAsync_CustomObjectId_MapsPublicIdToStorageId()
    {
        var stored = Feature.Create(99, null, ImmutableDictionary<string, object?>.Empty.Add("gid", 7L));
        var reader = Substitute.For<IFeatureReader>();
        reader.QueryAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(QueryResult<Feature>.Create(1, ImmutableArray.Create(stored))));

        var resolved = await GeoServicesFeatureObjectIdResolver.ResolveStorageIdsAsync(
            reader, SuccessFilters(), CustomObjectIdLayer(), 3, [7L], CancellationToken.None);

        resolved.Should().ContainKey(7);
        resolved[7].Should().Be(99);
        await reader.DidNotReceive().GetAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task ResolveStorageIdsAsync_StorageObjectId_IsIdentityAndDoesNotQuery()
    {
        var reader = Substitute.For<IFeatureReader>();

        var resolved = await GeoServicesFeatureObjectIdResolver.ResolveStorageIdsAsync(
            reader, SuccessFilters(), StorageObjectIdLayer(), 3, [7L], CancellationToken.None);

        resolved[7].Should().Be(7);
        await reader.DidNotReceive().QueryAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task RequireAttachmentFeature_CustomObjectId_ResolvesBeforeAttach()
    {
        var stored = Feature.Create(99, null, ImmutableDictionary<string, object?>.Empty.Add("gid", 7L));
        var reader = Substitute.For<IFeatureReader>();
        reader.GetAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Feature?>(new InvalidOperationException("public object id used as storage id")));
        reader.QueryAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(QueryResult<Feature>.Create(1, ImmutableArray.Create(stored))));
        var context = new DefaultHttpContext { Response = { Body = new MemoryStream() } };

        var feature = await AttachmentEndpoints.RequireAttachmentFeatureAsync(
            context,
            reader,
            SuccessFilters(),
            CustomObjectIdLayer(),
            3,
            7,
            CancellationToken.None);

        feature!.Value.Id.Should().Be(99);
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [UnitTest]
    public void VisibleAttachmentStorageIds_KeepsPublicIdsWhoseStorageIdIsVisible()
    {
        var visible = AttachmentEndpoints.VisibleAttachmentStorageIds(
            [7L, 8L],
            new Dictionary<long, long> { [7] = 99, [8] = 100 },
            [99L]);

        visible.Should().ContainSingle();
        visible[7].Should().Be(99);
    }

    [UnitTest]
    public async Task QueryAttachments_UsesStorageIdAndKeepsPublicParentObjectId()
    {
        var store = Substitute.For<IAttachmentStore>();
        store.ListAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Array.Empty<Attachment>()));
        var context = new DefaultHttpContext();

        var result = await AttachmentHandler.QueryAttachmentsAsync(
            3,
            [7L],
            returnUrl: false,
            AttachmentQueryFilter.None,
            store,
            NullLogger<AttachmentOperations>.Instance,
            context,
            CancellationToken.None,
            new Dictionary<long, long> { [7] = 99 });

        await store.Received(1).ListAsync(3, 99L, Arg.Any<CancellationToken>());
        await store.DidNotReceive().ListAsync(3, 7L, Arg.Any<CancellationToken>());
        var ok = result.Should().BeOfType<Ok<AttachmentQueryResponse>>().Subject;
        ok.Value!.AttachmentGroups.Should().ContainSingle();
        ok.Value.AttachmentGroups[0].ParentObjectId.Should().Be(7);
    }

    private static MetadataV2Resource CustomObjectIdLayer()
        => new()
        {
            SchemaFields =
            [
                new MetadataV2Field
                {
                    Name = "gid",
                    Type = MetadataV2FieldType.BigInteger,
                    SemanticRoles = ["id.primary"]
                }
            ]
        };

    private static MetadataV2Resource StorageObjectIdLayer()
        => new()
        {
            SchemaFields =
            [
                new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.BigInteger }
            ]
        };

    private static IFilterExpressionService SuccessFilters()
    {
        var filters = Substitute.For<IFilterExpressionService>();
        filters.Translate(Arg.Any<FilterExpression?>(), Arg.Any<MetadataV2Resource>())
            .Returns(FilterTranslationResult.Success(null, new SqlFragment("gid = @p", new object?[] { 7L })));
        return filters;
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

public sealed class FeatureServerReplicaServerStateCapturerTests
{
    [Fact]
    public async Task CaptureTokensAsync_CustomObjectId_ResolvesStoredRowAndKeysTokenByPublicId()
    {
        const int publicLayerId = 3;
        const int storageLayerId = 42;
        const long publicObjectId = 7001;
        const long internalObjectId = 19;

        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "assets", Name = "Assets" },
            Type = MetadataV2ResourceType.FeatureDataset,
            SchemaFields =
            [
                new MetadataV2Field
                {
                    Name = "asset_id",
                    Type = MetadataV2FieldType.BigInteger,
                    Nullable = false,
                    SemanticRoles = ["id.primary"],
                },
            ],
        };
        var stored = Feature.Create(
            internalObjectId,
            geometry: null,
            ImmutableDictionary<string, object?>.Empty.Add("asset_id", publicObjectId));
        var reader = Substitute.For<IFeatureReader>();
        reader.QueryAsync(storageLayerId, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(QueryResult<Feature>.Create(1, [stored])));
        var filters = Substitute.For<IFilterExpressionService>();
        filters.Translate(Arg.Any<FilterExpression>(), resource)
            .Returns(call => FilterTranslationResult.Success(
                call.Arg<FilterExpression>(),
                new SqlFragment("asset_id = @p0", [publicObjectId])));
        var sut = new FeatureServerReplicaServerStateCapturer(
            reader,
            filters,
            new Dictionary<int, MetadataV2Resource> { [publicLayerId] = resource });

        var tokens = await sut.CaptureTokensAsync(
            [new ReplicaConflictCaptureTarget(publicLayerId, storageLayerId, publicObjectId)]);

        tokens.Should().ContainSingle()
            .Which.Should().Be(new KeyValuePair<long, string>(publicObjectId, FeatureStateToken.Compute(stored)));
        await reader.DidNotReceive()
            .GetAsync(storageLayerId, publicObjectId, Arg.Any<CancellationToken>());
    }
}

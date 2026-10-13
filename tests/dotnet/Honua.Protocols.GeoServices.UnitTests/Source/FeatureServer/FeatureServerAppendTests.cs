// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;
using Honua.Protocols.GeoServices.FeatureServer;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Primitives;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

public sealed class FeatureServerAppendTests
{
    [UnitTest]
    public void TryReadAppendOptions_OmittedRollback_DefaultsTrue()
    {
        var read = FeatureServerEndpoints.TryReadAppendOptions(
            new Dictionary<string, StringValues>(),
            out var options,
            out var error);

        read.Should().BeTrue();
        error.Should().BeNull();
        options.RollbackOnFailure.Should().BeTrue();
        options.Upsert.Should().BeFalse();
    }

    [UnitTest]
    public void TryReadAppendOptions_ExplicitFalse_IsHonored()
    {
        var values = new Dictionary<string, StringValues> { ["rollbackOnFailure"] = "false" };

        var read = FeatureServerEndpoints.TryReadAppendOptions(values, out var options, out var error);

        read.Should().BeTrue();
        error.Should().BeNull();
        options.RollbackOnFailure.Should().BeFalse();
    }

    [UnitTest]
    public void CreateAppendApplyEditsRequest_ExplicitRollbackFalse_ReachesTheEditRequest()
    {
        var request = FeatureServerEndpoints.CreateAppendApplyEditsRequest([], [], rollbackOnFailure: false);

        request.RollbackOnFailure.Should().BeFalse();
        request.RollbackOnFailureExplicitlySet.Should().BeTrue();
        request.Adds.Should().NotBeNull();
        request.Adds!.Should().BeEmpty();
        request.Updates.Should().BeNull();
    }

    [UnitTest]
    public void PartitionAppendFeatures_ExistingKeyBecomesUpdate_MissingKeyStaysAdd()
    {
        var existing = new GeoServicesFeature
        {
            Attributes = new Dictionary<string, object?> { ["code"] = "a", ["name"] = "kept" }
        };
        var missing = new GeoServicesFeature
        {
            Attributes = new Dictionary<string, object?> { ["code"] = "b" }
        };
        var matches = new Dictionary<string, long>(StringComparer.Ordinal) { ["a"] = 15L };

        var (adds, updates) = FeatureServerEndpoints.PartitionAppendFeatures(
            [existing, missing],
            matches,
            "code",
            "objectid");

        adds.Should().ContainSingle();
        adds[0].Attributes["code"].Should().Be("b");
        updates.Should().ContainSingle();
        updates[0].Attributes["objectid"].Should().Be(15L);
        updates[0].Attributes["name"].Should().Be("kept");
    }

    [UnitTest]
    public async Task LoadAppendUpsertMatches_CustomObjectId_StampsPublicIdNotStorageId()
    {
        var resource = MatchResource();
        var stored = Feature.Create(
            99,
            null,
            ImmutableDictionary<string, object?>.Empty.Add("gid", 7L).Add("code", "a"));
        var reader = Substitute.For<IFeatureReader>();
        reader.QueryAsync(4, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(QueryResult<Feature>.Create(1, ImmutableArray.Create(stored))));
        var filters = Substitute.For<IFilterExpressionService>();
        filters.Translate(Arg.Any<FilterExpression?>(), Arg.Any<MetadataV2Resource>())
            .Returns(FilterTranslationResult.Success(null, new SqlFragment("code = @p", new object?[] { "a" })));

        var (matches, error) = await FeatureServerEndpoints.LoadAppendUpsertMatchesAsync(
            reader,
            filters,
            resource,
            4,
            [new GeoServicesFeature { Attributes = new Dictionary<string, object?> { ["code"] = "a" } }],
            "code",
            CancellationToken.None);

        error.Should().BeNull();
        matches.Should().NotBeNull();
        matches!["a"].Should().Be(7L);
    }

    [UnitTest]
    public async Task LoadAppendUpsertMatches_DuplicateKeys_AreRejected()
    {
        var resource = MatchResource();
        var first = Feature.Create(1, null, ImmutableDictionary<string, object?>.Empty.Add("gid", 7L).Add("code", "a"));
        var second = Feature.Create(2, null, ImmutableDictionary<string, object?>.Empty.Add("gid", 8L).Add("code", "a"));
        var reader = Substitute.For<IFeatureReader>();
        reader.QueryAsync(4, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(QueryResult<Feature>.Create(2, ImmutableArray.Create(first, second))));
        var filters = Substitute.For<IFilterExpressionService>();
        filters.Translate(Arg.Any<FilterExpression?>(), Arg.Any<MetadataV2Resource>())
            .Returns(FilterTranslationResult.Success(null, new SqlFragment("code = @p", new object?[] { "a" })));

        var (_, error) = await FeatureServerEndpoints.LoadAppendUpsertMatchesAsync(
            reader,
            filters,
            resource,
            4,
            [new GeoServicesFeature { Attributes = new Dictionary<string, object?> { ["code"] = "a" } }],
            "code",
            CancellationToken.None);

        error.Should().NotBeNullOrWhiteSpace();
        error.Should().Contain("more than one");
    }

    [UnitTest]
    public async Task LoadAppendUpsertMatches_DateTimeEpoch_MatchesAStoredTimestamp()
    {
        const long epoch = 1_700_000_000_000;
        var resource = TemporalResource(MetadataV2FieldType.DateTime);
        using var document = JsonDocument.Parse($"{{\"observed\":{epoch.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}");
        var incoming = document.RootElement.GetProperty("observed").Clone();
        var storedInstant = DateTime.SpecifyKind(
            DateTimeOffset.FromUnixTimeMilliseconds(epoch).UtcDateTime,
            DateTimeKind.Utc);
        var stored = Feature.Create(
            99,
            null,
            ImmutableDictionary<string, object?>.Empty.Add("gid", 15L).Add("observed", storedInstant));
        var reader = Substitute.For<IFeatureReader>();
        reader.QueryAsync(4, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(QueryResult<Feature>.Create(1, ImmutableArray.Create(stored))));
        var filters = AcceptingFilter(resource);

        var incomingFeature = new GeoServicesFeature
        {
            Attributes = new Dictionary<string, object?> { ["observed"] = incoming }
        };
        var (matches, error) = await FeatureServerEndpoints.LoadAppendUpsertMatchesAsync(
            reader,
            filters,
            resource,
            4,
            [incomingFeature],
            "observed",
            CancellationToken.None);

        error.Should().BeNull();
        var (adds, updates) = FeatureServerEndpoints.PartitionAppendFeatures(
            [incomingFeature],
            matches!,
            "observed",
            "gid",
            MetadataV2FieldType.DateTime);

        adds.Should().BeEmpty();
        updates.Should().ContainSingle();
        updates[0].Attributes["gid"].Should().Be(15L);
    }

    [UnitTest]
    public async Task LoadAppendUpsertMatches_CalendarDate_MatchesAStoredDate()
    {
        var resource = TemporalResource(MetadataV2FieldType.Date);
        var stored = Feature.Create(
            99,
            null,
            ImmutableDictionary<string, object?>.Empty.Add("gid", 15L).Add("observed", new DateOnly(2024, 3, 15)));
        var reader = Substitute.For<IFeatureReader>();
        reader.QueryAsync(4, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(QueryResult<Feature>.Create(1, ImmutableArray.Create(stored))));
        var filters = AcceptingFilter(resource);
        var incomingFeature = new GeoServicesFeature
        {
            Attributes = new Dictionary<string, object?> { ["observed"] = "2024-03-15" }
        };

        var (matches, error) = await FeatureServerEndpoints.LoadAppendUpsertMatchesAsync(
            reader,
            filters,
            resource,
            4,
            [incomingFeature],
            "observed",
            CancellationToken.None);

        error.Should().BeNull();
        var (_, updates) = FeatureServerEndpoints.PartitionAppendFeatures(
            [incomingFeature],
            matches!,
            "observed",
            "gid",
            MetadataV2FieldType.Date);

        updates.Should().ContainSingle();
        updates[0].Attributes["gid"].Should().Be(15L);
    }

    [UnitTest]
    public void FindUnsupportedAppendControl_RejectsFieldMappingsAndAllowsExplicitFalse()
    {
        FeatureServerEndpoints.FindUnsupportedAppendControl(
                new Dictionary<string, StringValues> { ["fieldMappings"] = "[{\"name\":\"a\"}]" })
            .Should().Be("fieldMappings");
        FeatureServerEndpoints.FindUnsupportedAppendControl(
                new Dictionary<string, StringValues> { ["skipUpdates"] = "false" })
            .Should().BeNull();
    }

    private static IFilterExpressionService AcceptingFilter(MetadataV2Resource resource)
    {
        var filters = Substitute.For<IFilterExpressionService>();
        filters.Translate(Arg.Any<FilterExpression?>(), Arg.Any<MetadataV2Resource>())
            .Returns(call =>
            {
                var expression = call.ArgAt<FilterExpression?>(0);
                FilterExpressionNormalizer.Normalize(expression!, resource);
                return FilterTranslationResult.Success(null, new SqlFragment("observed IN (@p0)", new object?[] { 1L }));
            });
        return filters;
    }

    private static MetadataV2Resource TemporalResource(MetadataV2FieldType observedType)
        => new()
        {
            SchemaFields =
            [
                new MetadataV2Field
                {
                    Name = "gid",
                    Type = MetadataV2FieldType.BigInteger,
                    SemanticRoles = ["id.primary"]
                },
                new MetadataV2Field { Name = "observed", Type = observedType }
            ]
        };

    private static MetadataV2Resource MatchResource()
        => new()
        {
            SchemaFields =
            [
                new MetadataV2Field
                {
                    Name = "gid",
                    Type = MetadataV2FieldType.BigInteger,
                    SemanticRoles = ["id.primary"]
                },
                new MetadataV2Field { Name = "code", Type = MetadataV2FieldType.String }
            ]
        };
}

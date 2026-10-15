// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Validation;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer.Services;

[global::Xunit.Trait("Tier", "Fast")]
public sealed class RelatedRecordsPagingTests
{
    [Fact]
    public void ValidateRelatedRecordsLimits_OmittedResultRecordCount_StaysNull()
    {
        var validator = new FeatureQueryValidator(new CommonQueryValidator(Options.Create(new LimitsOptions())));
        var result = validator.ValidateRelatedRecordsLimits(new QueryRelatedRecordsParameters
        {
            ObjectIds = [1, 2, 3],
            RelationshipId = 0
        });

        result.IsValid.Should().BeTrue();
        result.ValidatedParameters!.ResultRecordCount.Should().BeNull();
    }

    [Fact]
    public void ValidateRelatedRecordsLimits_ExplicitResultRecordCount_IsPreserved()
    {
        var validator = new FeatureQueryValidator(new CommonQueryValidator(Options.Create(new LimitsOptions())));
        var result = validator.ValidateRelatedRecordsLimits(new QueryRelatedRecordsParameters
        {
            ObjectIds = [1],
            RelationshipId = 0,
            ResultRecordCount = 5
        });

        result.IsValid.Should().BeTrue();
        result.ValidatedParameters!.ResultRecordCount.Should().Be(5);
    }

    [Fact]
    public void ValidateRelatedRecordsLimits_OverMaximumResultRecordCount_IsRejected()
    {
        var validator = new FeatureQueryValidator(new CommonQueryValidator(Options.Create(new LimitsOptions())));
        var result = validator.ValidateRelatedRecordsLimits(new QueryRelatedRecordsParameters
        {
            ObjectIds = [1],
            RelationshipId = 0,
            ResultRecordCount = 10001
        });

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("resultRecordCount");
    }

    [Fact]
    public void BuildRelatedQuery_OmittedResultRecordCount_UsesMaxRecordCount()
    {
        var service = new RelatedRecordsService(Substitute.For<IRelationshipStore>(), Options.Create(new LimitsOptions()));
        var query = service.BuildRelatedQuery(
            new QueryRelatedRecordsParameters { ObjectIds = [1, 2, 3], RelationshipId = 0 },
            [1, 2, 3],
            new MetadataV2Relationship
            {
                Id = "rel-1",
                RelatedResourceId = "child",
                OriginField = "objectid",
                DestinationField = "parent_id"
            },
            relatedStorageLayerId: 4,
            sqlFilter: null);

        query.Limit.Should().Be(new QueryLimits().MaxRecordCount);
    }

    [Fact]
    public void BuildRelatedQuery_ReturnCountOnly_OmitsLimit()
    {
        var service = new RelatedRecordsService(Substitute.For<IRelationshipStore>(), Options.Create(new LimitsOptions()));
        var query = service.BuildRelatedQuery(
            new QueryRelatedRecordsParameters
            {
                ObjectIds = [1],
                RelationshipId = 0,
                ReturnCountOnly = true,
                ResultRecordCount = 5
            },
            [1],
            new MetadataV2Relationship
            {
                Id = "rel-1",
                RelatedResourceId = "child",
                OriginField = "objectid",
                DestinationField = "parent_id"
            },
            relatedStorageLayerId: 4,
            sqlFilter: null);

        query.Limit.Should().BeNull();
    }

    [Fact]
    public void ApplyProbeWindow_PageCoversEveryRow_DoesNotSetHasMore()
    {
        var rows = Enumerable.Range(0, 1800).ToArray();
        var window = RelatedQuery.ApplyProbeWindow(new QueryLimits().MaxRecordCount, rows);

        window.Items.Should().HaveCount(1800);
        window.HasMoreResults.Should().BeFalse();
    }

    [Fact]
    public void ApplyProbeWindow_ProbeRowPastThePage_TrimsAndSetsHasMore()
    {
        var rows = Enumerable.Range(0, 1001).ToArray();
        var window = RelatedQuery.ApplyProbeWindow(1000, rows);

        window.Items.Should().HaveCount(1000);
        window.Items[0].Should().Be(0);
        window.HasMoreResults.Should().BeTrue();
    }

    [Fact]
    public void QueryRelatedRecordsResponse_WritesExceededTransferLimitWhenFalse()
    {
        var json = JsonSerializer.Serialize(
            new QueryRelatedRecordsResponse
            {
                RelatedRecordGroups = [],
                ExceededTransferLimit = false
            },
            FeatureServerJsonContext.Default.QueryRelatedRecordsResponse);

        json.Should().Contain("\"exceededTransferLimit\":false");
    }
}

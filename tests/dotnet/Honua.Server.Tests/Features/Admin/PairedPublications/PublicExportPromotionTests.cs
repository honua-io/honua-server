// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Publishing.Descriptors;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.Admin.PairedPublications;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Admin.PairedPublications;

[Collection("Unit")]
public sealed class PublicExportPromotionTests
{
    [UnitTest]
    public void CacheControl_PublicImmutable_UsesOneYearImmutableHeader()
    {
        StoredObjectCacheControl.For(StoredObjectCachePolicy.PublicImmutable)
            .Should().Be("public, max-age=31536000, immutable");
    }

    [UnitTest]
    public async Task PromoteAsync_WhenPublicDataDisabled_ReportsNotConfiguredWithoutError()
    {
        var harness = PromotionHarness.Create(new PromotionScenario { DestinationConfigured = false });

        var result = await harness.Promoter.PromoteAsync(harness.Input, CancellationToken.None);

        result.Status.Should().Be(PublicExportDeliveryStatus.NotConfigured);
        result.Reason.Should().Be(PublicExportPromoter.NotConfiguredMessage);
        harness.PublicStorage.Writes.Should().BeEmpty();
    }

    [UnitTest]
    public async Task PromoteAsync_WithTenancyEnabled_RefusesAndStaysSignedIn()
    {
        var harness = PromotionHarness.Create(new PromotionScenario { MultiTenancyEnabled = true });

        var result = await harness.Promoter.PromoteAsync(harness.Input, CancellationToken.None);

        result.Status.Should().Be(PublicExportDeliveryStatus.StaysSignedIn);
        result.Reason.Should().Be(PublicExportEligibility.ReasonMultiTenancyEnabled);
        harness.PublicStorage.Writes.Should().BeEmpty();
    }

    [UnitTest]
    public async Task PromoteAsync_WithAnonymousAccessDenied_RefusesAndStaysSignedIn()
    {
        var harness = PromotionHarness.Create(new PromotionScenario { AnonymousAllowed = false });

        var result = await harness.Promoter.PromoteAsync(harness.Input, CancellationToken.None);

        result.Status.Should().Be(PublicExportDeliveryStatus.StaysSignedIn);
        result.Reason.Should().Be(PublicExportEligibility.ReasonAnonymousDenied);
        harness.PublicStorage.Writes.Should().BeEmpty();
    }

    [UnitTest]
    public async Task PromoteAsync_WithPermanentRowFilter_RefusesAndStaysSignedIn()
    {
        var harness = PromotionHarness.Create(new PromotionScenario { PermanentFilter = "status = 'public'" });

        var result = await harness.Promoter.PromoteAsync(harness.Input, CancellationToken.None);

        result.Status.Should().Be(PublicExportDeliveryStatus.StaysSignedIn);
        result.Reason.Should().Be(PublicExportEligibility.ReasonRowFilter);
        harness.PublicStorage.Writes.Should().BeEmpty();
    }

    [UnitTest]
    public async Task PromoteAsync_WithAnonymousRlsFilter_RefusesAndStaysSignedIn()
    {
        var harness = PromotionHarness.Create(new PromotionScenario { RlsFilter = new SqlFragment("region = @p0", ["west"]) });

        var result = await harness.Promoter.PromoteAsync(harness.Input, CancellationToken.None);

        result.Status.Should().Be(PublicExportDeliveryStatus.StaysSignedIn);
        result.Reason.Should().Be(PublicExportEligibility.ReasonRowFilter);
        harness.PublicStorage.Writes.Should().BeEmpty();
    }

    [UnitTest]
    public async Task PromoteAsync_WithHiddenSchemaField_RefusesAndStaysSignedIn()
    {
        var harness = PromotionHarness.Create(new PromotionScenario { HasHiddenField = true });

        var result = await harness.Promoter.PromoteAsync(harness.Input, CancellationToken.None);

        result.Status.Should().Be(PublicExportDeliveryStatus.StaysSignedIn);
        result.Reason.Should().Be(PublicExportEligibility.ReasonHiddenField);
        harness.PublicStorage.Writes.Should().BeEmpty();
    }

    [UnitTest]
    public async Task PromoteAsync_WithAnonymousFieldMask_RefusesAndStaysSignedIn()
    {
        var harness = PromotionHarness.Create(new PromotionScenario { MaskedFields = ["owner_ssn"] });

        var result = await harness.Promoter.PromoteAsync(harness.Input, CancellationToken.None);

        result.Status.Should().Be(PublicExportDeliveryStatus.StaysSignedIn);
        result.Reason.Should().Be(PublicExportEligibility.ReasonHiddenField);
        harness.PublicStorage.Writes.Should().BeEmpty();
    }

    [UnitTest]
    public async Task PromoteAsync_WritesArtifactsThenCollectionThenRootCatalogLast_WithCorrectCachePolicies()
    {
        var harness = PromotionHarness.Create();

        var result = await harness.Promoter.PromoteAsync(harness.Input, CancellationToken.None);

        result.Status.Should().Be(PublicExportDeliveryStatus.Promoted);
        result.CollectionUrl.Should().Be("https://cdn.example.test/data/publications/parcels/v1/collection.json");
        result.CatalogUrl.Should().Be("https://cdn.example.test/data/catalog.json");

        harness.PublicStorage.Writes.Select(w => w.Key).Should().Equal(
            "data/publications/parcels/v1/parcels.pmtiles",
            "data/publications/parcels/v1/parcels.parquet",
            "data/publications/parcels/v1/README.md",
            "data/publications/parcels/v1/collection.json",
            "data/catalog.json");

        harness.PublicStorage.Writes.Take(4).Should().OnlyContain(w => w.CachePolicy == StoredObjectCachePolicy.PublicImmutable);
        harness.PublicStorage.Writes.Last().CachePolicy.Should().Be(StoredObjectCachePolicy.PublicReplacedInPlace);

        var catalog = JsonNode.Parse(harness.PublicStorage.Objects["data/catalog.json"])!.AsObject();
        catalog["type"]!.GetValue<string>().Should().Be("Catalog");
        catalog["stac_version"]!.GetValue<string>().Should().Be("1.1.0");
        catalog["links"]!.AsArray().Select(n => n!["href"]!.GetValue<string>())
            .Should().Contain("./publications/parcels/v1/collection.json");
    }

    [UnitTest]
    public async Task PromoteAsync_RetryAfterCrashBetweenArtifactsAndPointer_IsIdempotentAndSkipsUploadedObjects()
    {
        var harness = PromotionHarness.Create(new PromotionScenario
        {
            FailOnPublicKeyOnce = "data/publications/parcels/v1/collection.json"
        });

        var firstAttempt = () => harness.Promoter.PromoteAsync(harness.Input, CancellationToken.None);
        await firstAttempt.Should().ThrowAsync<InvalidOperationException>();

        harness.PublicStorage.Objects.Keys.Should().Contain("data/publications/parcels/v1/parcels.pmtiles");
        harness.PublicStorage.Objects.Keys.Should().Contain("data/publications/parcels/v1/parcels.parquet");
        harness.PublicStorage.Objects.Keys.Should().NotContain("data/catalog.json");
        var uploadsBeforeRetry = harness.PublicStorage.Writes.Count;

        var secondResult = await harness.Promoter.PromoteAsync(harness.Input, CancellationToken.None);

        secondResult.Status.Should().Be(PublicExportDeliveryStatus.Promoted);
        var retryWrites = harness.PublicStorage.Writes.Skip(uploadsBeforeRetry).Select(w => w.Key).ToArray();
        retryWrites.Should().Equal(
            "data/publications/parcels/v1/collection.json",
            "data/catalog.json");
    }

    [UnitTest]
    public async Task PromoteAsync_CanaryFailure_RecordsUnreadableAndPostsOpsFinding()
    {
        var harness = PromotionHarness.Create(new PromotionScenario
        {
            CanaryResult = PublicExportCanaryResult.Unreadable("Public URL returned HTTP 403.")
        });

        var result = await harness.Promoter.PromoteAsync(harness.Input, CancellationToken.None);

        result.Status.Should().Be(PublicExportDeliveryStatus.PromotedUnreadable);
        result.Reason.Should().Contain(OpsNotificationPublicExportNotifier.CanaryFindingTitle);
        result.Reason.Should().Contain("403");

        var saved = await harness.StateStore.GetPromotedAsync("parcels", CancellationToken.None);
        saved.Should().NotBeNull();
        saved!.CanaryState.Should().Be(PublicExportCanaryState.Unreadable);
        harness.CanaryNotices.Should().ContainSingle(n => n.PublicationId == "parcels" && n.Detail.Contains("403"));
    }
}

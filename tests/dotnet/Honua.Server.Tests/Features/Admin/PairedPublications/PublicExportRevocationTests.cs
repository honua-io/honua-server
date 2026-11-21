// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Server.Features.Admin.PairedPublications;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Admin.PairedPublications;

[Collection("Unit")]
public sealed class PublicExportRevocationTests
{
    [UnitTest]
    public async Task OnAdministrativeAccessChange_WhenLayerRestrictedByServiceName_DeletesObjectsRewritesCatalogInvalidatesAndCompletes()
    {
        var harness = RevocationHarness.Create(new RevocationScenario { AnonymousAllowed = false });

        // ServiceSettingsEndpoints passes service.Metadata.Name and layerIndex, not Metadata.Id / StorageLayerId.
        await harness.Service.OnAdministrativeAccessChangeAsync("parcels-svc", [0], CancellationToken.None);

        harness.PublicStorage.Objects.Keys.Where(k => k.StartsWith("data/publications/parcels/", StringComparison.Ordinal))
            .Should().BeEmpty();
        var catalog = JsonNode.Parse(harness.PublicStorage.Objects["data/catalog.json"])!.AsObject();
        catalog["links"]!.AsArray().Select(n => n!["href"]!.GetValue<string>())
            .Should().NotContain(href => href.Contains("parcels", StringComparison.Ordinal));
        catalog["links"]!.AsArray().Select(n => n!["href"]!.GetValue<string>())
            .Should().Contain("./publications/roads/v1/collection.json");

        harness.InvalidationRequests.Should().ContainSingle();
        harness.InvalidationRequests[0].Should().BeEquivalentTo(["/data/publications/parcels/*", "/data/catalog.json"]);
        harness.PollReceipts.Should().ContainSingle(r => r.ResourceId == "E123DIST" && r.InvalidationId == "INV-1");
        harness.StateStore.Revocations.Values.Should().ContainSingle(r => r.State == PublicExportRevocationState.Completed);
        harness.FailureNotices.Should().BeEmpty();
    }

    [UnitTest]
    public async Task CrashBetweenDeleteAndInvalidation_RestartResumesAndCreatesExactlyOneInvalidation()
    {
        var harness = RevocationHarness.Create(new RevocationScenario { AnonymousAllowed = false });
        var crashed = new PublicExportRevocationRecord
        {
            RevocationId = "rev-crash-1",
            PublicationId = "parcels",
            Reason = PublicExportRevocationService.ReasonAccessRestricted,
            ChangeId = "chg-101",
            State = PublicExportRevocationState.ObjectsDeleted,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await harness.StateStore.SaveRevocationAsync(crashed, CancellationToken.None);
        await harness.StateStore.DeletePromotedAsync("parcels", CancellationToken.None);

        await harness.Service.ResumePendingAsync(CancellationToken.None);

        harness.InvalidationRequests.Should().ContainSingle();
        harness.PollReceipts.Should().ContainSingle(r => r.InvalidationId == "INV-1");
        harness.StateStore.Revocations["rev-crash-1"].State.Should().Be(PublicExportRevocationState.Completed);
    }

    [UnitTest]
    public async Task CrashAfterInvalidationCreated_RestartPollsSameIdWithoutSecondInvalidation()
    {
        var harness = RevocationHarness.Create(new RevocationScenario { AnonymousAllowed = false });
        var crashed = new PublicExportRevocationRecord
        {
            RevocationId = "rev-crash-2",
            PublicationId = "parcels",
            Reason = PublicExportRevocationService.ReasonAccessRestricted,
            ChangeId = "chg-102",
            State = PublicExportRevocationState.InvalidationCreated,
            InvalidationId = "INV-EXISTING",
            DistributionId = "E123DIST",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await harness.StateStore.SaveRevocationAsync(crashed, CancellationToken.None);

        await harness.Service.ResumePendingAsync(CancellationToken.None);

        harness.InvalidationRequests.Should().BeEmpty();
        harness.PollReceipts.Should().ContainSingle(r => r.ResourceId == "E123DIST" && r.InvalidationId == "INV-EXISTING");
        harness.StateStore.Revocations["rev-crash-2"].State.Should().Be(PublicExportRevocationState.Completed);
    }

    [UnitTest]
    public async Task InvalidationFailure_PostsOpsNoticeAndRetriesToCompletion()
    {
        var harness = RevocationHarness.Create(new RevocationScenario
        {
            AnonymousAllowed = false,
            InvalidationOutcomes =
            [
                new CdnInvalidationResult(CdnInvalidationOutcome.Failed, "Throttled"),
                new CdnInvalidationResult(CdnInvalidationOutcome.Submitted, "INV-RETRY")
            ]
        });

        var first = await harness.Service.RevokeUnpublishedAsync("parcels", "chg-unpub", CancellationToken.None);

        first.State.Should().Be(PublicExportRevocationState.CatalogRewritten);
        first.AttemptCount.Should().Be(1);
        first.NextAttemptAt.Should().NotBeNull();
        harness.FailureNotices.Should().ContainSingle(n => n.Detail.Contains("Failed"));

        await harness.StateStore.SaveRevocationAsync(first with { NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(-1) }, CancellationToken.None);
        await harness.Service.ResumePendingAsync(CancellationToken.None);

        harness.StateStore.Revocations[first.RevocationId].State.Should().Be(PublicExportRevocationState.Completed);
        harness.PollReceipts.Should().ContainSingle(r => r.InvalidationId == "INV-RETRY");
    }

    [UnitTest]
    public async Task OnAdministrativeAccessChange_WhenLayerUnpublishedFromService_RevokesExport()
    {
        var harness = RevocationHarness.Create(new RevocationScenario
        {
            AnonymousAllowed = true,
            IncludeParcelsPublication = false
        });

        await harness.Service.OnAdministrativeAccessChangeAsync(serviceId: null, layerIds: null, CancellationToken.None);

        harness.PublicStorage.Objects.Keys.Where(k => k.StartsWith("data/publications/parcels/", StringComparison.Ordinal))
            .Should().BeEmpty();
        harness.StateStore.Revocations.Values.Should().ContainSingle(r =>
            r.PublicationId == "parcels" && r.State == PublicExportRevocationState.Completed);
        harness.StateStore.Promoted.Keys.Should().Equal("roads");
    }

    [UnitTest]
    public async Task UnpublishPath_DeletesPublicObjectsRewritesCatalogAndCompletes()
    {
        var harness = RevocationHarness.Create(new RevocationScenario { AnonymousAllowed = true });

        var result = await harness.Service.RevokeUnpublishedAsync("parcels", "chg-del", CancellationToken.None);

        result.Reason.Should().Be(PublicExportRevocationService.ReasonUnpublished);
        result.State.Should().Be(PublicExportRevocationState.Completed);
        harness.PublicStorage.Objects.Keys.Should().NotContain(k => k.StartsWith("data/publications/parcels/", StringComparison.Ordinal));
        harness.InvalidationRequests.Should().ContainSingle();
    }
}

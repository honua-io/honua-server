// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Server.Features.Studio.Export;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Honua.Server.Tests.Features.Studio;

/// <summary>
/// Regression coverage for honua-server#4908: a Studio deliverable export must fail loudly with a
/// machine-readable reason when it cannot render, rather than silently returning a "succeeded"
/// result over a blank artifact.
/// </summary>
[Protocol(TestProtocols.Studio)]
[Operation(Operations.StudioLifecycle)]
public sealed class StudioDeliverableExporterTests
{
    private static readonly Guid ItemId = Guid.NewGuid();

    [UnitTest]
    public async Task ExportAsync_WhenComposerCannotRender_ReturnsRenderUnavailableWithMachineReadableCode()
    {
        var lifecycle = BuildLifecycleMock();
        var storage = new Mock<ICloudFileStorage>(MockBehavior.Strict);
        var exporter = new StudioDeliverableExporter(
            lifecycle.Object,
            storage.Object,
            NullLogger<StudioDeliverableExporter>.Instance,
            compose: (_, _) => throw new StudioDeliverableRenderException(
                "studio_deliverable/no_renderable_typeface",
                "No rendering typeface with glyphs is available on this host."));

        var result = await exporter.ExportAsync(StudioPackageFamily.Map, ItemId, StudioDeliverableFormat.Png);

        result.Status.Should().Be(StudioDeliverableExportStatus.RenderUnavailable);
        result.Code.Should().Be("studio_deliverable/no_renderable_typeface");
        result.Detail.Should().Contain("rendering typeface");
        result.Artifact.Should().BeNull("a render failure must never carry a blank artifact");

        // Storage must never be touched for a render that never produced bytes.
        storage.VerifyNoOtherCalls();
    }

    [UnitTest]
    public async Task ExportAsync_WhenComposerRendersSuccessfully_StillReturnsSucceeded()
    {
        var lifecycle = BuildLifecycleMock();
        var storage = new Mock<ICloudFileStorage>(MockBehavior.Strict);
        var expectedArtifact = new StudioDeliverableArtifact
        {
            Content = [1, 2, 3],
            ContentType = "image/png",
            FileName = "map.png",
            Family = StudioPackageFamily.Map,
            Format = StudioDeliverableFormat.Png,
        };
        var exporter = new StudioDeliverableExporter(
            lifecycle.Object,
            storage.Object,
            NullLogger<StudioDeliverableExporter>.Instance,
            compose: (_, _) => expectedArtifact);

        var result = await exporter.ExportAsync(StudioPackageFamily.Map, ItemId, StudioDeliverableFormat.Png);

        result.Status.Should().Be(StudioDeliverableExportStatus.Succeeded);
        result.Artifact.Should().BeSameAs(expectedArtifact);
    }

    private static Mock<IStudioPackageLifecycleService> BuildLifecycleMock()
    {
        var version = new StudioContentVersion
        {
            ItemId = ItemId,
            PackageKey = "map-fixture",
            WorkspaceId = "studio",
            VersionId = Guid.NewGuid(),
            VersionNumber = 1,
            ContentHash = "hash",
            Envelope = new StudioPackageEnvelope
            {
                Family = StudioPackageFamily.Map,
                SchemaVersion = "1.0",
                Format = "fixture",
                Body = null,
            },
            Validation = StudioValidationSummary.NotValidated,
            CreatedAt = DateTimeOffset.UnixEpoch,
        };

        var lifecycle = new Mock<IStudioPackageLifecycleService>(MockBehavior.Strict);
        lifecycle
            .Setup(l => l.ListVersionsAsync(ItemId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<StudioContentVersion>)[version]);
        return lifecycle;
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Studio.Domain;
using Honua.Server.Features.Studio.Export;
using Honua.TestKit.Attributes;
using SkiaSharp;
using Xunit;

namespace Honua.Server.Tests.Features.Studio;

[Protocol(TestProtocols.Studio)]
[Operation(Operations.StudioLifecycle)]
public sealed class StudioDeliverableComposerTests
{
    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47];
    private static readonly byte[] PdfMagic = [0x25, 0x50, 0x44, 0x46]; // %PDF

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData(StudioPackageFamily.Map, StudioDeliverableFormat.Png)]
    [InlineData(StudioPackageFamily.Map, StudioDeliverableFormat.Pdf)]
    [InlineData(StudioPackageFamily.Dashboard, StudioDeliverableFormat.Png)]
    [InlineData(StudioPackageFamily.Dashboard, StudioDeliverableFormat.Pdf)]
    [InlineData(StudioPackageFamily.Report, StudioDeliverableFormat.Png)]
    [InlineData(StudioPackageFamily.Report, StudioDeliverableFormat.Pdf)]
    public void Compose_ForFamilyAndFormat_ProducesValidArtifact(StudioPackageFamily family, StudioDeliverableFormat format)
    {
        var version = BuildVersion(family);

        var artifact = StudioDeliverableComposer.Compose(version, format);

        artifact.Family.Should().Be(family);
        artifact.Format.Should().Be(format);
        artifact.Content.Should().NotBeEmpty();

        if (format == StudioDeliverableFormat.Pdf)
        {
            artifact.ContentType.Should().Be("application/pdf");
            artifact.FileName.Should().EndWith(".pdf");
            artifact.Content.Take(PdfMagic.Length).Should().Equal(PdfMagic);
        }
        else
        {
            artifact.ContentType.Should().Be("image/png");
            artifact.FileName.Should().EndWith(".png");
            artifact.Content.Take(PngMagic.Length).Should().Equal(PngMagic);
        }
    }

    [UnitTest]
    public void Compose_UsesBodyTitleForFileName()
    {
        var version = BuildVersion(StudioPackageFamily.Report);

        var artifact = StudioDeliverableComposer.Compose(version, StudioDeliverableFormat.Pdf);

        artifact.FileName.Should().Be("quarterly-report.pdf");
    }

    [UnitTest]
    public void Compose_WithMissingBody_StillProducesArtifact()
    {
        var version = BuildVersion(StudioPackageFamily.Map, body: null);

        var artifact = StudioDeliverableComposer.Compose(version, StudioDeliverableFormat.Png);

        artifact.Content.Take(PngMagic.Length).Should().Equal(PngMagic);
    }

    // honua-server#4908: prior to this guard, a typeface that resolved to a glyphless object
    // (no fonts installed on the AOT image) let StudioDeliverableComposer.Compose return a
    // 200-worthy PNG with only the two hairline rules drawn -- a silent blank artifact. The
    // guard must refuse loudly instead, for both "no typeface at all" (the attestation-marker
    // -removed case: RenderingTypeface.Default never resolved anything) and "typeface resolved
    // but carries zero glyphs" (the corrupted-font case: a handle exists but nothing can be
    // drawn with it).
    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData(null, false)]
    [InlineData(0, false)]
    [InlineData(6253, true)]
    public void HasRenderableGlyphs_ReflectsWhetherTypefaceCanDrawText(int? glyphCount, bool expected)
        => StudioDeliverableComposer.HasRenderableGlyphs(glyphCount).Should().Be(expected);

    [UnitTest]
    public void Compose_WithNoTypeface_ThrowsRenderExceptionRatherThanReturningABlankArtifact()
    {
        var version = BuildVersion(StudioPackageFamily.Map);

        var act = () => StudioDeliverableComposer.Compose(version, StudioDeliverableFormat.Png, typeface: null);

        act.Should().Throw<StudioDeliverableRenderException>()
            .Which.Code.Should().Be("studio_deliverable/no_renderable_typeface");
    }

    [UnitTest]
    public void Compose_WithRenderableTypeface_StillProducesArtifact()
    {
        var version = BuildVersion(StudioPackageFamily.Map);

        var artifact = StudioDeliverableComposer.Compose(version, StudioDeliverableFormat.Png, SKTypeface.Default);

        artifact.Content.Take(PngMagic.Length).Should().Equal(PngMagic);
    }

    private static StudioContentVersion BuildVersion(StudioPackageFamily family, string? body = "use-default")
    {
        var bodyJson = body switch
        {
            null => null,
            "use-default" => family switch
            {
                StudioPackageFamily.Map =>
                    """{"format":"honua_map_package.v1","title":"Parcels Overview","description":"Parcel coverage map.","layers":[{"title":"Parcels"},{"title":"Roads"}],"basemap":"streets"}""",
                StudioPackageFamily.Dashboard =>
                    """{"title":"Operations Dashboard","description":"Live metrics.","widgets":[{"title":"Throughput","type":"chart"}]}""",
                _ =>
                    """{"title":"Quarterly Report","summary":"Summary text.","sections":[{"heading":"Overview"},{"heading":"Findings"}]}""",
            },
            _ => body,
        };

        JsonElement? bodyElement = null;
        if (bodyJson is not null)
        {
            using var doc = JsonDocument.Parse(bodyJson);
            bodyElement = doc.RootElement.Clone();
        }

        return new StudioContentVersion
        {
            ItemId = Guid.NewGuid(),
            PackageKey = $"{family.ToString().ToLowerInvariant()}-fixture",
            WorkspaceId = "studio",
            VersionId = Guid.NewGuid(),
            VersionNumber = 1,
            ContentHash = "hash",
            Envelope = new StudioPackageEnvelope
            {
                Family = family,
                SchemaVersion = "1.0",
                Format = "fixture",
                Body = bodyElement,
            },
            Validation = StudioValidationSummary.NotValidated,
            CreatedAt = DateTimeOffset.UnixEpoch,
        };
    }
}

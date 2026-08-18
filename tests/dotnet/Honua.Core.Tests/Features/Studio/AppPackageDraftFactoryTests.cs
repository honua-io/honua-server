// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Studio.Drafts;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Studio;

/// <summary>
/// Unit tests for <see cref="AppPackageDraftFactory"/>, the deterministic
/// replacement for the retired prompt-driven app generation family (ADR-0076,
/// honua-server#3255). Alongside determinism they pin the closed-by-default
/// sharing posture the retained knowledge record (§5) requires never to regress.
/// </summary>
public sealed class AppPackageDraftFactoryTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    [UnitTest]
    public void CreateDraft_WithStructuredInput_ReturnsPackageWithStableAppIdentifier()
    {
        var result = CreateFactory().CreateDraft(FullRequest());

        Assert.Empty(result.Errors);
        var package = Assert.IsType<AppPackage>(result.Package);
        Assert.StartsWith("app_", package.AppPackageId, StringComparison.Ordinal);
    }

    [UnitTest]
    public void CreateDraft_ServerOwnsFormatAndStatusAndCreatedAt()
    {
        var package = CreateFactory().CreateDraft(FullRequest()).Package!;

        Assert.Equal("honua_app_package.v1", package.Format);
        Assert.Equal(PackageStatus.Draft, package.Status);
        Assert.Equal(FixedNow, package.CreatedAt);
    }

    [UnitTest]
    public void CreateDraft_SharingIsClosedByDefault()
    {
        // Retained knowledge §5: visibility private, embed false, reviewed false.
        var package = CreateFactory().CreateDraft(FullRequest()).Package!;

        Assert.NotNull(package.SharePolicy);
        Assert.Equal("private", package.SharePolicy!.Visibility);
        Assert.False(package.SharePolicy.Embed);
        Assert.False(package.SharePolicy.Reviewed);
    }

    [UnitTest]
    public void CreateDraft_HonoursTemplateBindingsAndRuntimeConfig()
    {
        var package = CreateFactory().CreateDraft(FullRequest()).Package!;

        Assert.Equal("analysis_dashboard", package.TemplateId);
        Assert.Equal("honua-sdk-js", package.TargetSdk);
        Assert.Equal("map_5a90", package.MapPackageId);
        Assert.Equal(["artifact_summary_report"], package.BoundArtifacts);
        Assert.Equal(
            "Flood Exposure Dashboard",
            package.RuntimeConfig!.Value.GetProperty("title").GetString());
    }

    [UnitTest]
    public void CreateDraft_WithIdenticalInput_IsDeterministic()
    {
        var factory = CreateFactory();

        var first = factory.CreateDraft(FullRequest()).Package!;
        var second = factory.CreateDraft(FullRequest()).Package!;

        Assert.Equal(Serialize(first), Serialize(second));
    }

    [UnitTest]
    public void CreateDraft_RequestCarriesNoNaturalLanguageSurface()
    {
        Assert.DoesNotContain(
            typeof(AppPackageDraftRequest).GetProperties(),
            property => property.Name.Contains("Prompt", StringComparison.OrdinalIgnoreCase));
    }

    [UnitTest]
    public void CreateDraft_WithoutTargetSdk_DefaultsToTheStandardsDeclaredDefault()
    {
        var package = CreateFactory().CreateDraft(FullRequest() with { TargetSdk = null }).Package!;

        Assert.Equal("honua-sdk-js", package.TargetSdk);
    }

    [UnitTest]
    public void CreateDraft_WithoutMapPackage_WarnsButStillProducesADraft()
    {
        // Retained knowledge §2: an unresolved binding defers to publish.
        var result = CreateFactory().CreateDraft(FullRequest() with { MapPackageId = null });

        Assert.Empty(result.Errors);
        Assert.NotNull(result.Package);
        Assert.Contains(result.Warnings, warning => warning.Code == "bindingNotResolved");
    }

    [UnitTest]
    public void CreateDraft_WithBlankMapPackageId_IsRejected()
    {
        var result = CreateFactory().CreateDraft(FullRequest() with { MapPackageId = " " });

        Assert.Null(result.Package);
        Assert.Contains(result.Errors, error => error.Code == "mapPackageIdInvalid");
    }

    [UnitTest]
    public void CreateDraft_WithBlankBoundArtifactId_IsRejected()
    {
        var result = CreateFactory().CreateDraft(FullRequest() with { BoundArtifactIds = ["  "] });

        Assert.Null(result.Package);
        Assert.Contains(result.Errors, error => error.Code == "boundArtifactIdInvalid");
    }

    [UnitTest]
    public void CreateDraft_WithDuplicateBoundArtifactIds_IsRejected()
    {
        var result = CreateFactory().CreateDraft(FullRequest() with
        {
            BoundArtifactIds = ["artifact_a", "artifact_a"]
        });

        Assert.Null(result.Package);
        Assert.Contains(result.Errors, error => error.Code == "boundArtifactIdDuplicate");
    }

    [UnitTest]
    public void CreateDraft_WithNonObjectRuntimeConfig_IsRejected()
    {
        using var document = JsonDocument.Parse("[1,2,3]");

        var result = CreateFactory().CreateDraft(FullRequest() with
        {
            RuntimeConfig = document.RootElement.Clone()
        });

        Assert.Null(result.Package);
        Assert.Contains(result.Errors, error => error.Code == "runtimeConfigInvalid");
    }

    [UnitTest]
    public void CreateDraft_PreservesBoundArtifactOrder()
    {
        var package = CreateFactory().CreateDraft(FullRequest() with
        {
            BoundArtifactIds = ["artifact_b", "artifact_a"]
        }).Package!;

        Assert.Equal(["artifact_b", "artifact_a"], package.BoundArtifacts);
    }

    private static AppPackageDraftFactory CreateFactory() => new(
        new StubIdentifierGenerator(),
        new FixedTimeProvider(FixedNow));

    private static AppPackageDraftRequest FullRequest()
    {
        using var document = JsonDocument.Parse("""{"title":"Flood Exposure Dashboard"}""");
        return new AppPackageDraftRequest
        {
            TemplateId = "analysis_dashboard",
            TargetSdk = "honua-sdk-js",
            MapPackageId = "map_5a90",
            BoundArtifactIds = ["artifact_summary_report"],
            RuntimeConfig = document.RootElement.Clone()
        };
    }

    private static string Serialize(AppPackage package) =>
        JsonSerializer.Serialize(package, PackagingJsonContext.Default.AppPackage);

    /// <summary>Pinned identifier generator so a draft is reproducible byte for byte.</summary>
    private sealed class StubIdentifierGenerator : IDraftIdentifierGenerator
    {
        public string NewIdentifier(string prefix) => prefix + "_fixed0001";
    }

    /// <summary>Pinned clock.</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Core.Tests.Features.ControlPlane;

/// <summary>
/// Tests for the platform-release desired-state model that co-versions the serving and
/// geoprocessing planes (ADR-0060 WS2): projection precedence, worker matching, co-versioning
/// validation, and the cross-plane skew snapshot.
/// </summary>
public sealed class PlatformReleaseProjectionTests
{
    private static PlatformReleaseDefinition CoVersionedRelease(string version = "2026.07.0") => new()
    {
        Version = version,
        ServingArtifactReference = $"ghcr.io/honua/server:{version}",
        Workers =
        [
            new PlatformReleaseWorkerImage { ArtifactReference = $"ghcr.io/honua/worker:{version}" },
            new PlatformReleaseWorkerImage { RuntimeProfile = "gdal", ArtifactReference = $"ghcr.io/honua/worker-gdal:{version}" }
        ]
    };

    [Fact]
    public void ResolveServingArtifact_ExplicitPin_Wins()
    {
        var result = PlatformReleaseProjection.ResolveServingArtifact("pinned:image", CoVersionedRelease());
        Assert.Equal("pinned:image", result);
    }

    [Fact]
    public void ResolveServingArtifact_NoExplicit_UsesRelease()
    {
        var result = PlatformReleaseProjection.ResolveServingArtifact(null, CoVersionedRelease());
        Assert.Equal("ghcr.io/honua/server:2026.07.0", result);
    }

    [Fact]
    public void ResolveServingArtifact_NoRelease_ReturnsNull()
    {
        Assert.Null(PlatformReleaseProjection.ResolveServingArtifact(null, release: null));
    }

    [Fact]
    public void ResolveWorkerArtifact_MatchesRuntimeProfile()
    {
        var result = PlatformReleaseProjection.ResolveWorkerArtifact(null, "gdal", CoVersionedRelease());
        Assert.Equal("ghcr.io/honua/worker-gdal:2026.07.0", result);
    }

    [Fact]
    public void ResolveWorkerArtifact_UnmatchedProfile_FallsBackToDefaultImage()
    {
        var result = PlatformReleaseProjection.ResolveWorkerArtifact(null, "python", CoVersionedRelease());
        Assert.Equal("ghcr.io/honua/worker:2026.07.0", result);
    }

    [Fact]
    public void ResolveWorkerArtifact_ExplicitPin_Wins()
    {
        var result = PlatformReleaseProjection.ResolveWorkerArtifact("pinned:worker", "gdal", CoVersionedRelease());
        Assert.Equal("pinned:worker", result);
    }

    [Fact]
    public void ResolveWorker_ProfileMatchIsCaseInsensitive()
    {
        var worker = CoVersionedRelease().ResolveWorker("GDAL");
        Assert.NotNull(worker);
        Assert.Equal("ghcr.io/honua/worker-gdal:2026.07.0", worker!.ArtifactReference);
    }

    [Fact]
    public void ResolveWorker_NoDefaultImage_UnmatchedProfileReturnsNull()
    {
        var release = new PlatformReleaseDefinition
        {
            Version = "2026.07.0",
            ServingArtifactReference = "ghcr.io/honua/server:2026.07.0",
            Workers = [new PlatformReleaseWorkerImage { RuntimeProfile = "gdal", ArtifactReference = "ghcr.io/honua/worker-gdal:2026.07.0" }]
        };

        Assert.Null(release.ResolveWorker("python"));
    }

    [Fact]
    public void BuildSkewSnapshot_AllProjectedFromRelease_IsCoVersioned()
    {
        var serving = new[] { new PlatformReleasePlaneEntry { Id = "serving-prod" } };
        var execution = new[]
        {
            new PlatformReleasePlaneEntry { Id = "gp-gdal", RuntimeProfile = "gdal" },
            new PlatformReleasePlaneEntry { Id = "gp-default" }
        };

        var snapshot = PlatformReleaseProjection.BuildSkewSnapshot(CoVersionedRelease(), serving, execution);

        Assert.True(snapshot.ReleaseDeclared);
        Assert.True(snapshot.IsCoVersioned);
        Assert.Empty(snapshot.SkewedIds);
        Assert.Equal("2026.07.0", snapshot.ReleaseVersion);
        Assert.All(snapshot.Serving, p => Assert.True(p.ProjectedFromRelease));
        Assert.All(snapshot.Execution, p => Assert.True(p.ProjectedFromRelease));
        Assert.Equal("ghcr.io/honua/worker-gdal:2026.07.0",
            snapshot.Execution.Single(p => p.Id == "gp-gdal").EffectiveArtifactReference);
    }

    [Fact]
    public void BuildSkewSnapshot_ExplicitDivergentPin_IsSkew()
    {
        var serving = new[]
        {
            new PlatformReleasePlaneEntry { Id = "serving-prod", ExplicitArtifactReference = "ghcr.io/honua/server:old" }
        };
        var execution = new[] { new PlatformReleasePlaneEntry { Id = "gp-default" } };

        var snapshot = PlatformReleaseProjection.BuildSkewSnapshot(CoVersionedRelease(), serving, execution);

        Assert.False(snapshot.IsCoVersioned);
        Assert.Contains("serving-prod", snapshot.SkewedIds);
        var servingProjection = snapshot.Serving.Single();
        Assert.True(servingProjection.Skewed);
        Assert.False(servingProjection.ProjectedFromRelease);
        Assert.Equal("ghcr.io/honua/server:old", servingProjection.EffectiveArtifactReference);
    }

    [Fact]
    public void BuildSkewSnapshot_ExplicitPinMatchingRelease_IsNotSkew()
    {
        var serving = new[]
        {
            new PlatformReleasePlaneEntry { Id = "serving-prod", ExplicitArtifactReference = "ghcr.io/honua/server:2026.07.0" }
        };

        var snapshot = PlatformReleaseProjection.BuildSkewSnapshot(
            CoVersionedRelease(),
            serving,
            Array.Empty<PlatformReleasePlaneEntry>());

        Assert.True(snapshot.IsCoVersioned);
        Assert.Empty(snapshot.SkewedIds);
    }

    [Fact]
    public void BuildSkewSnapshot_NoRelease_IsNotCoVersioned()
    {
        var snapshot = PlatformReleaseProjection.BuildSkewSnapshot(
            release: null,
            new[] { new PlatformReleasePlaneEntry { Id = "serving-prod", ExplicitArtifactReference = "x" } },
            Array.Empty<PlatformReleasePlaneEntry>());

        Assert.False(snapshot.ReleaseDeclared);
        Assert.False(snapshot.IsCoVersioned);
        Assert.Empty(snapshot.SkewedIds);
    }

    [Fact]
    public void Validate_CoVersionedRelease_HasNoFailures()
    {
        var failures = new List<string>();
        PlatformReleaseValidation.Validate(CoVersionedRelease(), "ControlPlane:PlatformRelease", failures);
        Assert.Empty(failures);
    }

    [Fact]
    public void Validate_NullRelease_HasNoFailures()
    {
        var failures = new List<string>();
        PlatformReleaseValidation.Validate(release: null, "ControlPlane:PlatformRelease", failures);
        Assert.Empty(failures);
    }

    [Fact]
    public void Validate_ServingWithoutWorkers_Fails()
    {
        var release = new PlatformReleaseDefinition
        {
            Version = "2026.07.0",
            ServingArtifactReference = "ghcr.io/honua/server:2026.07.0"
        };

        var failures = new List<string>();
        PlatformReleaseValidation.Validate(release, "ControlPlane:PlatformRelease", failures);

        Assert.Contains(failures, f => f.Contains("co-version", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_MissingVersion_Fails()
    {
        var release = new PlatformReleaseDefinition
        {
            Version = string.Empty,
            ServingArtifactReference = "ghcr.io/honua/server:2026.07.0",
            Workers = [new PlatformReleaseWorkerImage { ArtifactReference = "ghcr.io/honua/worker:2026.07.0" }]
        };

        var failures = new List<string>();
        PlatformReleaseValidation.Validate(release, "ControlPlane:PlatformRelease", failures);

        Assert.Contains(failures, f => f.Contains("Version"));
    }

    [Fact]
    public void Validate_DuplicateRuntimeProfiles_Fails()
    {
        var release = new PlatformReleaseDefinition
        {
            Version = "2026.07.0",
            ServingArtifactReference = "ghcr.io/honua/server:2026.07.0",
            Workers =
            [
                new PlatformReleaseWorkerImage { RuntimeProfile = "gdal", ArtifactReference = "a" },
                new PlatformReleaseWorkerImage { RuntimeProfile = "GDAL", ArtifactReference = "b" }
            ]
        };

        var failures = new List<string>();
        PlatformReleaseValidation.Validate(release, "ControlPlane:PlatformRelease", failures);

        Assert.Contains(failures, f => f.Contains("unique", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_EmptyWorkerArtifact_Fails()
    {
        var release = new PlatformReleaseDefinition
        {
            Version = "2026.07.0",
            ServingArtifactReference = "ghcr.io/honua/server:2026.07.0",
            Workers = [new PlatformReleaseWorkerImage { RuntimeProfile = "gdal", ArtifactReference = "  " }]
        };

        var failures = new List<string>();
        PlatformReleaseValidation.Validate(release, "ControlPlane:PlatformRelease", failures);

        Assert.Contains(failures, f => f.Contains("ArtifactReference"));
    }
}

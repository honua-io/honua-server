// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.ControlPlane.Domain;

/// <summary>
/// Versioned platform-release desired state that binds the two operability planes to a single
/// release version (ADR-0060, workstream 2). The serving half declares the artifact the serving
/// deploy targets converge to; the execution half declares the geoprocessing worker image(s) the
/// GP plane submits. Because both planes project their <see cref="DeployTargetDefinition.ArtifactReference"/>
/// / <see cref="ExecutionJobDefinition.ArtifactReference"/> from this one document, an upgrade is a
/// single diff that bumps serving and worker images together rather than two independent edits that
/// can drift.
/// </summary>
public sealed record PlatformReleaseDefinition
{
    /// <summary>
    /// Stable release version that co-versions both planes (for example <c>2026.07.0</c>). All plane
    /// artifacts declared here advance together when this value changes.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    /// Serving-plane artifact/image reference this release projects onto deploy targets that do not
    /// pin an explicit artifact of their own. Null when the release does not declare the serving half.
    /// </summary>
    public string? ServingArtifactReference { get; init; }

    /// <summary>
    /// Geoprocessing worker images declared by this release, keyed by runtime profile. A worker image
    /// with a null/empty <see cref="PlatformReleaseWorkerImage.RuntimeProfile"/> is the default image
    /// used for workloads that do not name a runtime profile or whose profile has no explicit match.
    /// </summary>
    public IReadOnlyList<PlatformReleaseWorkerImage> Workers { get; init; } = Array.Empty<PlatformReleaseWorkerImage>();

    /// <summary>
    /// Whether the release declares a serving-plane artifact.
    /// </summary>
    public bool DeclaresServing => !string.IsNullOrWhiteSpace(ServingArtifactReference);

    /// <summary>
    /// Whether the release declares at least one geoprocessing worker image.
    /// </summary>
    public bool DeclaresWorkers => Workers.Count > 0;

    /// <summary>
    /// Resolves the worker image this release projects for the supplied runtime profile. An exact
    /// (case-insensitive) profile match wins; otherwise the default (null/empty profile) image is
    /// returned when one is declared. Returns null when no applicable worker image exists.
    /// </summary>
    /// <param name="runtimeProfile">Workload runtime profile, or null for the default image.</param>
    /// <returns>The matching worker image, or null.</returns>
    public PlatformReleaseWorkerImage? ResolveWorker(string? runtimeProfile)
    {
        PlatformReleaseWorkerImage? defaultImage = null;
        foreach (var worker in Workers)
        {
            if (string.IsNullOrWhiteSpace(worker.RuntimeProfile))
            {
                defaultImage ??= worker;
                continue;
            }

            if (!string.IsNullOrWhiteSpace(runtimeProfile) &&
                string.Equals(worker.RuntimeProfile, runtimeProfile, StringComparison.OrdinalIgnoreCase))
            {
                return worker;
            }
        }

        return defaultImage;
    }
}

/// <summary>
/// A single geoprocessing worker image declared by a <see cref="PlatformReleaseDefinition"/>.
/// </summary>
public sealed record PlatformReleaseWorkerImage
{
    /// <summary>
    /// Runtime profile (workload family) this image serves, for example <c>gdal</c> or <c>python</c>.
    /// Null or empty declares the default image applied when a workload's profile has no explicit match.
    /// </summary>
    public string? RuntimeProfile { get; init; }

    /// <summary>
    /// Worker container image / artifact reference the GP plane submits for this runtime profile.
    /// </summary>
    public required string ArtifactReference { get; init; }
}

/// <summary>
/// One plane element (a serving deploy target or a GP execution workload) considered for
/// platform-release projection, used to compute the cross-plane skew snapshot.
/// </summary>
public sealed record PlatformReleasePlaneEntry
{
    /// <summary>
    /// Stable identifier of the plane element (deploy target id or execution workload id).
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Runtime profile declared by the element, used to match a worker image for execution entries.
    /// Ignored for serving entries.
    /// </summary>
    public string? RuntimeProfile { get; init; }

    /// <summary>
    /// Artifact reference explicitly pinned on the element, or null when the element defers to the
    /// release projection.
    /// </summary>
    public string? ExplicitArtifactReference { get; init; }
}

/// <summary>
/// Projection of a single plane element after the platform release has been applied. Surfaces
/// whether the element's effective artifact came from the release (converged) or from an explicit
/// pin that diverges from the release (skew), so a mid-upgrade operator can see both planes align.
/// </summary>
public sealed record PlatformReleasePlaneProjection
{
    /// <summary>
    /// Stable identifier of the plane element.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Runtime profile of the element, if any.
    /// </summary>
    public string? RuntimeProfile { get; init; }

    /// <summary>
    /// Effective artifact reference after applying explicit-pin-wins precedence over the release.
    /// </summary>
    public string? EffectiveArtifactReference { get; init; }

    /// <summary>
    /// Whether the effective artifact was projected from the release rather than an explicit pin.
    /// </summary>
    public required bool ProjectedFromRelease { get; init; }

    /// <summary>
    /// Whether the element pins an explicit artifact that diverges from what the release projects for
    /// it (an operator-visible skew during a partial upgrade).
    /// </summary>
    public required bool Skewed { get; init; }
}

/// <summary>
/// Cross-plane consistency snapshot for the declared platform release. Lets an operator (and later
/// the copilot) see the release version each plane currently projects and whether serving and worker
/// images are co-versioned or still converging.
/// </summary>
public sealed record PlatformReleaseSkewSnapshot
{
    /// <summary>
    /// Declared release version, or null when no platform release is configured.
    /// </summary>
    public string? ReleaseVersion { get; init; }

    /// <summary>
    /// Whether a platform release is declared for this instance.
    /// </summary>
    public required bool ReleaseDeclared { get; init; }

    /// <summary>
    /// Serving-plane projections (one per deploy target considered).
    /// </summary>
    public IReadOnlyList<PlatformReleasePlaneProjection> Serving { get; init; } = Array.Empty<PlatformReleasePlaneProjection>();

    /// <summary>
    /// Execution-plane projections (one per GP execution workload considered).
    /// </summary>
    public IReadOnlyList<PlatformReleasePlaneProjection> Execution { get; init; } = Array.Empty<PlatformReleasePlaneProjection>();

    /// <summary>
    /// Whether both planes are aligned on the declared release: true only when a release is declared
    /// and no plane element pins an artifact that diverges from the release projection.
    /// </summary>
    public required bool IsCoVersioned { get; init; }

    /// <summary>
    /// Identifiers of plane elements pinning an artifact that diverges from the release projection.
    /// </summary>
    public IReadOnlyList<string> SkewedIds { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Pure projection helpers that apply a <see cref="PlatformReleaseDefinition"/> to the serving and
/// execution planes. Precedence is always "explicit pin wins": a plane element that declares its own
/// artifact keeps it; only elements without an explicit artifact inherit the release projection.
/// </summary>
public static class PlatformReleaseProjection
{
    /// <summary>
    /// Resolves the effective serving artifact for a deploy target: the explicit artifact when set,
    /// otherwise the release's serving artifact.
    /// </summary>
    /// <param name="explicitArtifact">Artifact explicitly pinned on the deploy target.</param>
    /// <param name="release">Declared platform release, or null.</param>
    /// <returns>Effective serving artifact reference, or null.</returns>
    public static string? ResolveServingArtifact(string? explicitArtifact, PlatformReleaseDefinition? release)
        => !string.IsNullOrWhiteSpace(explicitArtifact)
            ? explicitArtifact
            : release?.ServingArtifactReference;

    /// <summary>
    /// Resolves the effective worker artifact for an execution workload: the explicit artifact when
    /// set, otherwise the release worker image matched by runtime profile (falling back to the
    /// release default image).
    /// </summary>
    /// <param name="explicitArtifact">Artifact explicitly pinned on the workload.</param>
    /// <param name="runtimeProfile">Workload runtime profile.</param>
    /// <param name="release">Declared platform release, or null.</param>
    /// <returns>Effective worker artifact reference, or null.</returns>
    public static string? ResolveWorkerArtifact(
        string? explicitArtifact,
        string? runtimeProfile,
        PlatformReleaseDefinition? release)
        => !string.IsNullOrWhiteSpace(explicitArtifact)
            ? explicitArtifact
            : release?.ResolveWorker(runtimeProfile)?.ArtifactReference;

    /// <summary>
    /// Builds the cross-plane skew snapshot from the declared release and the raw serving/execution
    /// plane entries. An element is "skewed" when it pins an explicit artifact that diverges from the
    /// artifact the release would project for it.
    /// </summary>
    /// <param name="release">Declared platform release, or null.</param>
    /// <param name="servingEntries">Serving-plane entries (deploy targets).</param>
    /// <param name="executionEntries">Execution-plane entries (GP workloads).</param>
    /// <returns>The computed skew snapshot.</returns>
    public static PlatformReleaseSkewSnapshot BuildSkewSnapshot(
        PlatformReleaseDefinition? release,
        IReadOnlyList<PlatformReleasePlaneEntry> servingEntries,
        IReadOnlyList<PlatformReleasePlaneEntry> executionEntries)
    {
        ArgumentNullException.ThrowIfNull(servingEntries);
        ArgumentNullException.ThrowIfNull(executionEntries);

        var skewedIds = new List<string>();

        var serving = new List<PlatformReleasePlaneProjection>(servingEntries.Count);
        foreach (var entry in servingEntries)
        {
            var releaseArtifact = release?.ServingArtifactReference;
            serving.Add(BuildProjection(entry, releaseArtifact, skewedIds));
        }

        var execution = new List<PlatformReleasePlaneProjection>(executionEntries.Count);
        foreach (var entry in executionEntries)
        {
            var releaseArtifact = release?.ResolveWorker(entry.RuntimeProfile)?.ArtifactReference;
            execution.Add(BuildProjection(entry, releaseArtifact, skewedIds));
        }

        var releaseDeclared = release is not null;

        return new PlatformReleaseSkewSnapshot
        {
            ReleaseVersion = release?.Version,
            ReleaseDeclared = releaseDeclared,
            Serving = serving,
            Execution = execution,
            IsCoVersioned = releaseDeclared && skewedIds.Count == 0,
            SkewedIds = skewedIds
        };
    }

    private static PlatformReleasePlaneProjection BuildProjection(
        PlatformReleasePlaneEntry entry,
        string? releaseArtifact,
        List<string> skewedIds)
    {
        var hasExplicit = !string.IsNullOrWhiteSpace(entry.ExplicitArtifactReference);
        var hasRelease = !string.IsNullOrWhiteSpace(releaseArtifact);
        var effective = hasExplicit ? entry.ExplicitArtifactReference : releaseArtifact;

        var skewed = hasExplicit &&
            hasRelease &&
            !string.Equals(entry.ExplicitArtifactReference, releaseArtifact, StringComparison.Ordinal);
        if (skewed)
        {
            skewedIds.Add(entry.Id);
        }

        return new PlatformReleasePlaneProjection
        {
            Id = entry.Id,
            RuntimeProfile = entry.RuntimeProfile,
            EffectiveArtifactReference = effective,
            ProjectedFromRelease = !hasExplicit && hasRelease,
            Skewed = skewed
        };
    }
}

/// <summary>
/// Co-versioning validation for a declared <see cref="PlatformReleaseDefinition"/>. Enforces that a
/// release binds both planes: a release that bumps serving must also declare worker images (and vice
/// versa), so an upgrade cannot silently advance one plane while leaving the other behind.
/// </summary>
public static class PlatformReleaseValidation
{
    /// <summary>
    /// Validates a declared platform release, appending any failures to <paramref name="failures"/>.
    /// A null release (no release configured) is valid and produces no failures.
    /// </summary>
    /// <param name="release">Declared platform release, or null when none is configured.</param>
    /// <param name="propertyPrefix">Configuration property prefix used in failure messages.</param>
    /// <param name="failures">Failure sink appended to in place.</param>
    public static void Validate(
        PlatformReleaseDefinition? release,
        string propertyPrefix,
        List<string> failures)
    {
        ArgumentNullException.ThrowIfNull(failures);

        if (release is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(release.Version))
        {
            failures.Add($"{propertyPrefix}:Version cannot be empty when a platform release is declared.");
        }

        // Co-versioning: both planes must be declared together so an upgrade bumps them as one diff.
        if (release.DeclaresServing && !release.DeclaresWorkers)
        {
            failures.Add(
                $"{propertyPrefix} declares a serving artifact but no worker images; a platform release must co-version both planes.");
        }

        if (release.DeclaresWorkers && !release.DeclaresServing)
        {
            failures.Add(
                $"{propertyPrefix} declares worker images but no serving artifact; a platform release must co-version both planes.");
        }

        if (!release.DeclaresServing && !release.DeclaresWorkers)
        {
            failures.Add(
                $"{propertyPrefix} must declare a serving artifact and at least one worker image.");
        }

        var seenProfiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < release.Workers.Count; i++)
        {
            var worker = release.Workers[i];
            var workerPrefix = $"{propertyPrefix}:Workers:{i}";

            if (string.IsNullOrWhiteSpace(worker.ArtifactReference))
            {
                failures.Add($"{workerPrefix}:ArtifactReference cannot be empty.");
            }

            // Null/empty runtime profile is the single default image; treat it as a distinct key.
            var profileKey = string.IsNullOrWhiteSpace(worker.RuntimeProfile)
                ? string.Empty
                : worker.RuntimeProfile.Trim();
            if (!seenProfiles.Add(profileKey))
            {
                var label = profileKey.Length == 0 ? "(default)" : profileKey;
                failures.Add($"{workerPrefix}:RuntimeProfile '{label}' must be unique across worker images.");
            }
        }
    }
}

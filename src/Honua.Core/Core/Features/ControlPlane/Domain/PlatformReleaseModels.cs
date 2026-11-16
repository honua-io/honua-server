// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.ControlPlane.Domain;

public sealed record PlatformReleaseDefinition
{
    public required string Version { get; init; }

    public string? ServingArtifactReference { get; init; }

    public IReadOnlyList<PlatformReleaseWorkerImage> Workers { get; init; } = Array.Empty<PlatformReleaseWorkerImage>();

    public bool DeclaresServing => !string.IsNullOrWhiteSpace(ServingArtifactReference);

    public bool DeclaresWorkers => Workers.Count > 0;

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

public sealed record PlatformReleaseWorkerImage
{
    public string? RuntimeProfile { get; init; }

    public required string ArtifactReference { get; init; }
}

public sealed record PlatformReleasePlaneEntry
{
    public required string Id { get; init; }

    public string? RuntimeProfile { get; init; }

    public string? ExplicitArtifactReference { get; init; }
}

public sealed record PlatformReleasePlaneProjection
{
    public required string Id { get; init; }

    public string? RuntimeProfile { get; init; }

    public string? EffectiveArtifactReference { get; init; }

    public required bool ProjectedFromRelease { get; init; }

    public required bool Skewed { get; init; }
}

public sealed record PlatformReleaseSkewSnapshot
{
    public string? ReleaseVersion { get; init; }

    public required bool ReleaseDeclared { get; init; }

    public IReadOnlyList<PlatformReleasePlaneProjection> Serving { get; init; } = Array.Empty<PlatformReleasePlaneProjection>();

    public IReadOnlyList<PlatformReleasePlaneProjection> Execution { get; init; } = Array.Empty<PlatformReleasePlaneProjection>();

    public required bool IsCoVersioned { get; init; }

    public IReadOnlyList<string> SkewedIds { get; init; } = Array.Empty<string>();
}

public static class PlatformReleaseProjection
{
    public static string? ResolveServingArtifact(string? explicitArtifact, PlatformReleaseDefinition? release)
        => !string.IsNullOrWhiteSpace(explicitArtifact)
            ? explicitArtifact
            : release?.ServingArtifactReference;

    public static string? ResolveWorkerArtifact(
        string? explicitArtifact,
        string? runtimeProfile,
        PlatformReleaseDefinition? release)
        => !string.IsNullOrWhiteSpace(explicitArtifact)
            ? explicitArtifact
            : release?.ResolveWorker(runtimeProfile)?.ArtifactReference;

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

public static class PlatformReleaseValidation
{
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

public static class PlatformReleaseVersion
{
    private const string TagPrefix = "honua-";

    public static string? Normalize(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var value = version.Trim();
        if (value.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase))
        {
            value = value[TagPrefix.Length..];
        }

        if (value.Length > 1 && (value[0] == 'v' || value[0] == 'V') && char.IsAsciiDigit(value[1]))
        {
            value = value[1..];
        }

        var suffixStart = value.IndexOfAny(['-', '+']);
        var core = suffixStart < 0 ? value : value[..suffixStart];
        var suffix = suffixStart < 0 ? string.Empty : value[suffixStart..];
        var parts = core.Split('.');
        if (parts.Length == 3 && parts[2] == "0")
        {
            core = string.Concat(parts[0], ".", parts[1]);
        }

        return string.Concat(core, suffix).ToLowerInvariant();
    }

    public static bool Matches(string? declaredVersion, string? targetVersion)
    {
        var declared = Normalize(declaredVersion);
        return declared is not null && string.Equals(declared, Normalize(targetVersion), StringComparison.Ordinal);
    }
}

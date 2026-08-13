// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Orchestration.Domain;
using System.Globalization;

namespace Honua.Server.Features.Orchestration;

/// <summary>
/// Resolves declarative step input bindings into concrete analysis-plan input values
/// using the captured output artifacts of upstream steps.
/// </summary>
internal static class WorkflowBindingResolver
{
    private const string ArtifactSelectorPrefix = "artifact:";

    public sealed record BindingResolution(
        IReadOnlyDictionary<string, string> ResolvedValues,
        IReadOnlyDictionary<string, RasterSourceDescriptor> ResolvedRasterSources,
        IReadOnlyList<string> Failures);

    public static BindingResolution Resolve(
        WorkflowStepDefinition step,
        IReadOnlyDictionary<string, WorkflowStepState> upstreamByStepId,
        RasterSecurityContextReference rasterSecurityContext)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(upstreamByStepId);

        if (step.InputBindings.Count == 0)
        {
            return new BindingResolution(
                new Dictionary<string, string>(StringComparer.Ordinal),
                new Dictionary<string, RasterSourceDescriptor>(StringComparer.Ordinal),
                Array.Empty<string>());
        }

        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        var rasterSources = new Dictionary<string, RasterSourceDescriptor>(StringComparer.Ordinal);
        var failures = new List<string>();

        foreach (var binding in step.InputBindings)
        {
            if (!upstreamByStepId.TryGetValue(binding.SourceStepId, out var upstream))
            {
                failures.Add($"Binding '{binding.TargetInputKey}' references unknown upstream step '{binding.SourceStepId}'.");
                continue;
            }

            var artifact = SelectArtifact(upstream.OutputArtifacts, binding.SourceArtifactSelector);
            if (artifact is null)
            {
                failures.Add(
                    $"Binding '{binding.TargetInputKey}' could not resolve selector '{binding.SourceArtifactSelector}' from step '{binding.SourceStepId}'.");
                continue;
            }

            if (IsStaged(artifact))
            {
                if (artifact.Uri is null)
                {
                    failures.Add(
                        $"Binding '{binding.TargetInputKey}' cannot use staged artifact '{artifact.ArtifactId}' from step '{binding.SourceStepId}' because its content is unavailable on this host.");
                    continue;
                }

                if (!TryCreateStagedRasterSource(artifact, rasterSecurityContext, out var source, out var reason))
                {
                    failures.Add(
                        $"Binding '{binding.TargetInputKey}' cannot use staged artifact '{artifact.ArtifactId}' from step '{binding.SourceStepId}': {reason}");
                    continue;
                }

                rasterSources[binding.TargetInputKey] = source;
                continue;
            }

            if (artifact.Uri is not null)
            {
                resolved[binding.TargetInputKey] = artifact.Uri;
                continue;
            }

            resolved[binding.TargetInputKey] = artifact.ArtifactId;
        }

        return new BindingResolution(resolved, rasterSources, failures);
    }

    private static bool IsStaged(ArtifactRef artifact)
        => artifact.Metadata.TryGetValue(RasterOutputArtifactMetadata.Staged, out var staged)
            && string.Equals(staged, "true", StringComparison.OrdinalIgnoreCase);

    private static bool TryCreateStagedRasterSource(
        ArtifactRef artifact,
        RasterSecurityContextReference securityContext,
        out StagedArtifactRasterSourceDescriptor source,
        out string reason)
    {
        source = null!;
        reason = "its durable content identity is incomplete";
        if (artifact.Kind != ArtifactKind.Raster)
        {
            reason = "only staged raster artifacts can be bound as raster inputs";
            return false;
        }

        var metadata = artifact.Metadata;
        if (!Enum.TryParse<CloudStorageProvider>(
                metadata.GetValueOrDefault(RasterOutputArtifactMetadata.StoreProvider),
                ignoreCase: true,
                out var provider)
            || !Enum.IsDefined(provider)
            || string.IsNullOrWhiteSpace(metadata.GetValueOrDefault(RasterOutputArtifactMetadata.StoreReference))
            || string.IsNullOrWhiteSpace(metadata.GetValueOrDefault(RasterOutputArtifactMetadata.ObjectKey))
            || !long.TryParse(metadata.GetValueOrDefault(RasterOutputArtifactMetadata.SizeBytes), NumberStyles.None, CultureInfo.InvariantCulture, out var sizeBytes)
            || sizeBytes <= 0
            || !TryParsePositiveLong(metadata, RasterOutputArtifactMetadata.GridWidth, out var width)
            || !TryParsePositiveLong(metadata, RasterOutputArtifactMetadata.GridHeight, out var height)
            || !TryParsePositiveInt(metadata, RasterOutputArtifactMetadata.GridBandCount, out var bandCount)
            || !TryParsePositiveInt(metadata, RasterOutputArtifactMetadata.GridBitsPerSample, out var bitsPerSample)
            || !TryParseChecksum(metadata.GetValueOrDefault(RasterOutputArtifactMetadata.Checksum), out var checksum))
        {
            return false;
        }

        RasterSourcePixelScale? pixelScale = null;
        var hasScaleX = metadata.TryGetValue(RasterOutputArtifactMetadata.GridPixelScaleX, out var scaleXRaw);
        var hasScaleY = metadata.TryGetValue(RasterOutputArtifactMetadata.GridPixelScaleY, out var scaleYRaw);
        if (hasScaleX != hasScaleY)
        {
            return false;
        }

        if (hasScaleX)
        {
            if (!double.TryParse(scaleXRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var scaleX)
                || !double.TryParse(scaleYRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var scaleY)
                || !double.IsFinite(scaleX)
                || !double.IsFinite(scaleY)
                || scaleX <= 0d
                || scaleY <= 0d)
            {
                return false;
            }

            pixelScale = new RasterSourcePixelScale(scaleX, scaleY);
        }

        var mediaType = metadata.GetValueOrDefault(RasterOutputArtifactMetadata.MediaType)
            ?? artifact.ContentType;
        if (mediaType is null
            || !string.Equals(mediaType, "image/tiff", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(checksum.Algorithm, "sha256", StringComparison.Ordinal))
        {
            reason = "only SHA-256-addressed GeoTIFF outputs can be reused as raster inputs";
            return false;
        }

        source = new StagedArtifactRasterSourceDescriptor
        {
            ArtifactReference = artifact.ArtifactId,
            Provider = provider,
            StoreReference = metadata[RasterOutputArtifactMetadata.StoreReference],
            ObjectKey = metadata[RasterOutputArtifactMetadata.ObjectKey],
            Version = $"{checksum.Algorithm}:{checksum.Value}",
            Content = new RasterContentIdentity
            {
                SizeBytes = sizeBytes,
                MediaType = mediaType,
                Checksum = checksum,
            },
            SecurityContext = securityContext with { },
            DeclaredDimensions = new RasterSourceDimensions(width, height, bandCount, bitsPerSample),
            DeclaredPixelScale = pixelScale,
        };
        return true;
    }

    private static bool TryParsePositiveLong(
        IReadOnlyDictionary<string, string> metadata,
        string key,
        out long value)
        => long.TryParse(metadata.GetValueOrDefault(key), NumberStyles.None, CultureInfo.InvariantCulture, out value)
            && value > 0;

    private static bool TryParsePositiveInt(
        IReadOnlyDictionary<string, string> metadata,
        string key,
        out int value)
        => int.TryParse(metadata.GetValueOrDefault(key), NumberStyles.None, CultureInfo.InvariantCulture, out value)
            && value > 0;

    private static bool TryParseChecksum(string? raw, out RasterChecksum checksum)
    {
        checksum = null!;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var separator = raw.IndexOf(':');
        if (separator <= 0 || separator == raw.Length - 1)
        {
            return false;
        }

        checksum = new RasterChecksum(raw[..separator], raw[(separator + 1)..]);
        return true;
    }

    public static ArtifactRef? SelectArtifact(
        IReadOnlyList<ArtifactRef>? artifacts,
        string selector)
    {
        if (artifacts is null || artifacts.Count == 0 || string.IsNullOrWhiteSpace(selector))
        {
            return null;
        }

        if (!selector.StartsWith(ArtifactSelectorPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var target = selector[ArtifactSelectorPrefix.Length..].Trim();
        if (string.IsNullOrEmpty(target))
        {
            return null;
        }

        if (int.TryParse(target, out var index))
        {
            return index >= 0 && index < artifacts.Count ? artifacts[index] : null;
        }

        return artifacts.FirstOrDefault(artifact => string.Equals(artifact.Label, target, StringComparison.OrdinalIgnoreCase));
    }

    public static AnalysisPlan ApplyBindings(AnalysisPlan plan, BindingResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if ((resolution.ResolvedValues.Count == 0 && resolution.ResolvedRasterSources.Count == 0)
            || plan.Steps.Count == 0)
        {
            return plan;
        }

        // Apply resolved inputs to the first executable step of the plan. The canonical
        // AnalysisPlanStep dictionary is the transport surface the job substrate reads,
        // so overriding there keeps the engine free of opinion about downstream routing.
        var firstStep = plan.Steps[0];
        var merged = new Dictionary<string, string>(firstStep.Inputs, StringComparer.Ordinal);
        var rasterSources = new Dictionary<string, RasterSourceDescriptor>(
            firstStep.RasterSources,
            StringComparer.Ordinal);
        foreach (var pair in resolution.ResolvedValues)
        {
            merged[pair.Key] = pair.Value;
            rasterSources.Remove(pair.Key);
        }

        foreach (var pair in resolution.ResolvedRasterSources)
        {
            merged.Remove(pair.Key);
            rasterSources[pair.Key] = pair.Value;
        }

        var steps = plan.Steps.ToArray();
        steps[0] = firstStep with { Inputs = merged, RasterSources = rasterSources };
        return plan with { Steps = steps };
    }
}

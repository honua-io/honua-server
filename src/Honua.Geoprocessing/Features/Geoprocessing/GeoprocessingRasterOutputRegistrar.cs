// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.ControlPlane;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Geoprocessing;

/// <summary>
/// Performs the optional post-success registration of staged raster outputs into the
/// COG catalog (#3089). Registration is an explicit, durable, idempotently reconciled
/// cross-store step per ADR-0071: the staged object (already committed at an immutable
/// attempt-scoped key) is the authoritative winner and the catalog row is reconciled
/// to it. The <c>uq_cloud_raster_object</c> unique constraint on
/// <c>(layer_id, provider, bucket, object_key)</c> makes a retried or replayed
/// registration converge on one row instead of duplicating it. Callers gate result
/// visibility on this step: a package for a job with registration intents is neither
/// persisted nor returned until every intent is durably registered, so a successful
/// result can never reference an unregistered output.
/// </summary>
internal sealed partial class GeoprocessingRasterOutputRegistrar(
    IServiceScopeFactory serviceScopeFactory,
    ILogger<GeoprocessingRasterOutputRegistrar> logger,
    Honua.Core.Features.Geoprocessing.Abstractions.IGeoprocessingOutputObjectStore? outputStore = null)
{
    private const string CogCatalogTargetPrefix = "cog-catalog:";
    private const string PostgisTargetPrefix = "postgis:";

    /// <summary>Whether the job spec declares any output registration intents.</summary>
    public static bool HasRegistrationIntents(ExecutionJobRecord job)
        => job.Spec.Parameters.Keys.Any(static key => key.StartsWith(
            ExecutionJobParameterKeys.GeoprocessingOutputRegistrationPrefix, StringComparison.Ordinal));

    /// <summary>
    /// Ensures every declared registration intent is durably satisfied and returns the
    /// result package enriched with the registered catalog identities. Throws when an
    /// intent cannot be satisfied so callers never expose a Completed package whose
    /// requested registration did not happen.
    /// </summary>
    /// <param name="job">Terminal Succeeded job record.</param>
    /// <param name="package">Result package built from the job.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The package with registration metadata applied.</returns>
    public async Task<AnalysisResultPackage> EnsureRegisteredAsync(
        ExecutionJobRecord job,
        AnalysisResultPackage package,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(package);

        if (job.Status != ExecutionJobStatus.Succeeded)
        {
            return package;
        }

        var intents = CollectIntents(job);
        if (intents.Count == 0)
        {
            return package;
        }

        var descriptors = CollectDescriptors(job);
        var registeredByOutput = new Dictionary<string, (int LayerId, long RasterId)>(StringComparer.Ordinal);

        using var scope = serviceScopeFactory.CreateScope();
        var cogStore = scope.ServiceProvider.GetService<ICogStore>();

        foreach (var (outputName, target) in intents)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!descriptors.TryGetValue(outputName, out var descriptor))
            {
                throw new GeoprocessingValidationException(
                    $"Output registration for '{outputName}' cannot complete: the job published no typed "
                    + "output descriptor with that name.");
            }

            if (target.StartsWith(PostgisTargetPrefix, StringComparison.Ordinal))
            {
                // Fail closed: PostGIS materialization of staged outputs requires the
                // durable staged large-raster ingest contract (#3098). Never silently
                // drop a requested registration.
                throw new GeoprocessingValidationException(
                    $"Output registration for '{outputName}' requested the PostGIS raster store, which is "
                    + "not yet supported for staged outputs (#3098). Register into the COG catalog instead.");
            }

            if (!target.StartsWith(CogCatalogTargetPrefix, StringComparison.Ordinal)
                || !int.TryParse(
                    target.AsSpan(CogCatalogTargetPrefix.Length),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var layerId)
                || layerId <= 0)
            {
                throw new GeoprocessingValidationException(
                    $"Output registration for '{outputName}' has an invalid target '{target}'. Expected "
                    + $"'{CogCatalogTargetPrefix}{{layerId}}'.");
            }

            if (descriptor is not StagedObjectRasterOutputDescriptor staged)
            {
                throw new GeoprocessingValidationException(
                    $"Output registration for '{outputName}' requires a staged object artifact; "
                    + $"'{descriptor.GetType().Name}' outputs cannot register into the COG catalog.");
            }

            if (RasterOutputDescriptorValidator.LooksLikeZarr(staged.Content.MediaType, staged.ObjectKey))
            {
                // Defense in depth: publication already rejects Zarr fail-closed (#3103);
                // a Zarr row must never reach cloud_raster_catalog.
                throw new GeoprocessingValidationException(
                    $"Output registration for '{outputName}' names a Zarr hierarchy, which cannot be "
                    + "registered into the COG catalog (#3103).");
            }

            if (cogStore is null)
            {
                throw new InvalidOperationException(
                    "Output registration requires the COG catalog store, which is not configured in this "
                    + "deployment.");
            }

            var rasterId = await RegisterOrGetAsync(
                cogStore, layerId, staged, cancellationToken).ConfigureAwait(false);

            // The catalog row is permanent while the job record expires from Redis, so
            // the durable retention hold on the staged object — not the job record —
            // is what exempts a registered object from orphan sweeping. Setting the
            // hold is part of registration: failing it fails this pass, and the
            // idempotent replay (terminal callback or next results read) completes it.
            await EnsureRetentionHoldAsync(job.OperationId, outputName, staged, cancellationToken)
                .ConfigureAwait(false);

            registeredByOutput[outputName] = (layerId, rasterId);
            Log.OutputRegistered(logger, job.OperationId, outputName, layerId, rasterId);
        }

        return ApplyRegistrationMetadata(package, registeredByOutput);
    }

    private async Task EnsureRetentionHoldAsync(
        string jobId,
        string outputName,
        StagedObjectRasterOutputDescriptor staged,
        CancellationToken cancellationToken)
    {
        if (!RasterOutputContentRoutes.CanServe(outputStore, staged.Provider.ToString(), staged.StoreReference))
        {
            // No matching store on this host: the hold cannot be written here. This is
            // the split-host misconfiguration surfaced by the content route as well;
            // log loudly rather than failing registration for a store-topology issue.
            Log.RetentionHoldStoreUnavailable(logger, jobId, outputName);
            return;
        }

        if (!await outputStore!.SetRetentionHoldAsync(staged.ObjectKey, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"Registered output '{outputName}' of job '{jobId}' references staged object "
                + $"'{staged.ObjectKey}' which no longer exists in the output store.");
        }
    }

    /// <summary>
    /// Registers the staged object into the COG catalog, converging on the existing row
    /// when a previous attempt, a retried callback, or a concurrent replay already
    /// registered the same immutable (layer, provider, bucket, key) identity.
    /// </summary>
    private static async Task<long> RegisterOrGetAsync(
        ICogStore cogStore,
        int layerId,
        StagedObjectRasterOutputDescriptor staged,
        CancellationToken cancellationToken)
    {
        var existing = await FindExistingAsync(cogStore, layerId, staged, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing.Value;
        }

        try
        {
            var registered = await cogStore.RegisterAsync(
                new CogRegistrationRequest
                {
                    LayerId = layerId,
                    Name = $"gp-output:{staged.JobId}:{staged.OutputName}",
                    Description = $"Geoprocessing output '{staged.OutputName}' of job '{staged.JobId}' "
                        + $"(attempt {staged.AttemptNumber}).",
                    Provider = staged.Provider,
                    Bucket = staged.StoreReference,
                    ObjectKey = staged.ObjectKey,
                },
                cancellationToken).ConfigureAwait(false);
            return registered.Id;
        }
        catch (InvalidOperationException)
        {
            // uq_cloud_raster_object won the race for a concurrent replay: the row for
            // this exact immutable object identity already exists — converge on it.
            var raced = await FindExistingAsync(cogStore, layerId, staged, cancellationToken).ConfigureAwait(false);
            if (raced is not null)
            {
                return raced.Value;
            }

            throw;
        }
    }

    private static async Task<long?> FindExistingAsync(
        ICogStore cogStore,
        int layerId,
        StagedObjectRasterOutputDescriptor staged,
        CancellationToken cancellationToken)
    {
        var registrations = await cogStore.ListByLayerAsync(layerId, cancellationToken).ConfigureAwait(false);
        return registrations
            .FirstOrDefault(candidate =>
                candidate.Provider == staged.Provider
                && string.Equals(candidate.Bucket, staged.StoreReference, StringComparison.Ordinal)
                && string.Equals(candidate.ObjectKey, staged.ObjectKey, StringComparison.Ordinal))
            ?.Id;
    }

    private static Dictionary<string, string> CollectIntents(ExecutionJobRecord job)
    {
        var intents = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in job.Spec.Parameters)
        {
            if (!key.StartsWith(
                    ExecutionJobParameterKeys.GeoprocessingOutputRegistrationPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var outputName = key[ExecutionJobParameterKeys.GeoprocessingOutputRegistrationPrefix.Length..];
            if (!string.IsNullOrWhiteSpace(outputName) && !string.IsNullOrWhiteSpace(value))
            {
                intents[outputName] = value.Trim();
            }
        }

        return intents;
    }

    private static Dictionary<string, RasterOutputDescriptor> CollectDescriptors(ExecutionJobRecord job)
    {
        var descriptors = new Dictionary<string, RasterOutputDescriptor>(StringComparer.Ordinal);
        foreach (var reference in job.ArtifactReferences)
        {
            if (RasterOutputJson.TryDeserialize(reference, out var descriptor))
            {
                descriptors[descriptor.OutputName] = descriptor;
            }
        }

        return descriptors;
    }

    private static AnalysisResultPackage ApplyRegistrationMetadata(
        AnalysisResultPackage package,
        Dictionary<string, (int LayerId, long RasterId)> registeredByOutput)
    {
        if (registeredByOutput.Count == 0)
        {
            return package;
        }

        var artifacts = package.Artifacts.Select(artifact =>
        {
            if (!artifact.Metadata.TryGetValue(RasterOutputArtifactMetadata.OutputName, out var outputName)
                || !registeredByOutput.TryGetValue(outputName, out var registration))
            {
                return artifact;
            }

            var metadata = new Dictionary<string, string>(artifact.Metadata, StringComparer.Ordinal)
            {
                [RasterOutputArtifactMetadata.RegisteredLayerId] =
                    registration.LayerId.ToString(CultureInfo.InvariantCulture),
                [RasterOutputArtifactMetadata.RegisteredCatalogRasterId] =
                    registration.RasterId.ToString(CultureInfo.InvariantCulture),
            };
            return artifact with { Metadata = metadata };
        }).ToArray();

        return package with { Artifacts = artifacts };
    }

    private static partial class Log
    {
        [LoggerMessage(8031, LogLevel.Information,
            "Registered geoprocessing output '{OutputName}' of job {OperationId} into the COG catalog (layer {LayerId}, raster {RasterId})")]
        public static partial void OutputRegistered(
            ILogger logger, string operationId, string outputName, int layerId, long rasterId);

        [LoggerMessage(8037, LogLevel.Warning,
            "No matching staged output store on this host for registered output '{OutputName}' of job {OperationId}; the retention hold cannot be written here and the object may be swept once the job record expires. Configure Geoprocessing:OutputStaging identically on the serving host.")]
        public static partial void RetentionHoldStoreUnavailable(ILogger logger, string operationId, string outputName);
    }
}

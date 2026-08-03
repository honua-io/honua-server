// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.ObjectModel;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Postgres.Features.Raster;

/// <summary>
/// Fail-closed provider declaration for the four PostGIS operations mapped by RAST-011.
/// </summary>
/// <remarks>
/// The SQL primitives exist, but the authenticated submit path cannot yet admit a typed PostGIS
/// source and prepare an attempt-fenced output publication intent. Advertising these routes as
/// available before those boundaries exist would make an unsafe and unreachable path selectable.
/// </remarks>
internal sealed class PostgisSurfaceZonalRasterProviderExecutor : IRasterProviderExecutor
{
    internal const string ProviderId = "postgis";
    internal const string PolicyVersion = "postgis-raster-v1";
    internal const string UnavailableCode = "postgis-surface-zonal-unavailable";
    internal const string UnavailableReason =
        "PostGIS surface and zonal execution awaits authenticated typed-source admission, "
        + "tenant-scoped resource authorization, and attempt-fenced output publication.";

    private static readonly ReadOnlyCollection<RasterProviderCapability> _capabilities =
        Array.AsReadOnly(PostgisSurfaceZonalExecutionContract.ProcessIds
            .Select(CreateCapability)
            .ToArray());

    /// <inheritdoc />
    public IReadOnlyList<RasterProviderCapability> Capabilities => _capabilities;

    /// <inheritdoc />
    public Task<RasterProviderExecutionResult> ExecuteAsync(
        RasterProviderExecutionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(RasterProviderExecutionResult.CapabilityUnavailable(
            UnavailableCode,
            UnavailableReason));
    }

    private static RasterProviderCapability CreateCapability(string processId) => new()
    {
        ProviderId = ProviderId,
        Engine = RasterEngine.Postgis,
        Variant = new RasterSemanticVariant
        {
            ProcessId = processId,
            SemanticVersion = PostgisSurfaceZonalExecutionContract.SemanticVersion,
            ImplementationVersion =
                $"honua.postgis.{processId}@{PostgisSurfaceZonalExecutionContract.SemanticVersion}",
        },
        PolicyVersion = PolicyVersion,
        Availability = RasterProviderAvailability.Unavailable,
        UnavailabilityReason = UnavailableReason,
    };
}

/// <summary>
/// Execution-owned surface output target prepared after tenant and resource authorization.
/// </summary>
/// <remarks>
/// This value must never be constructed directly from caller parameters. A future active provider
/// will obtain it from the attempt-fenced output publication path.
/// </remarks>
internal sealed record PostgisPreparedSurfaceOutput(int LayerId, string Name);

/// <summary>Typed result returned by the operation-to-primitive mapping.</summary>
internal sealed record PostgisSurfaceZonalPrimitiveResult
{
    /// <summary>Persisted surface result for slope, aspect, or hillshade.</summary>
    public SurfaceAnalysisResult? Surface { get; init; }

    /// <summary>Aggregate rows for zonal statistics.</summary>
    public IReadOnlyList<RasterZonalStatisticsRow>? ZonalStatistics { get; init; }
}

/// <summary>
/// Maps validated RAST-011 bindings to existing PostGIS raster primitives.
/// </summary>
/// <remarks>
/// This class performs no admission, tenant authorization, output authorization, or publication.
/// It is deliberately separate from the unavailable provider declaration so the primitive mapping
/// can be proved without making the incomplete execution route selectable.
/// </remarks>
internal sealed class PostgisSurfaceZonalPrimitiveDispatcher(
    ISurfaceAnalysisService surfaceAnalysis,
    IRasterStore rasterStore)
{
    /// <summary>Executes exactly one previously validated primitive mapping.</summary>
    public async Task<PostgisSurfaceZonalPrimitiveResult> ExecuteAsync(
        PostgisSurfaceZonalBinding binding,
        PostgisPreparedSurfaceOutput? surfaceOutput,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);

        return binding switch
        {
            PostgisSlopeBinding slope => new PostgisSurfaceZonalPrimitiveResult
            {
                Surface = await surfaceAnalysis.ComputeSlopeAsync(
                    CreateSurfaceRequest(slope.Source, RequireSurfaceOutput(surfaceOutput)),
                    slope.Units,
                    slope.ZFactor,
                    cancellationToken).ConfigureAwait(false),
            },
            PostgisAspectBinding aspect => new PostgisSurfaceZonalPrimitiveResult
            {
                Surface = await surfaceAnalysis.ComputeAspectAsync(
                    CreateSurfaceRequest(aspect.Source, RequireSurfaceOutput(surfaceOutput)),
                    cancellationToken).ConfigureAwait(false),
            },
            PostgisHillshadeBinding hillshade => new PostgisSurfaceZonalPrimitiveResult
            {
                Surface = await surfaceAnalysis.ComputeHillshadeAsync(
                    CreateSurfaceRequest(hillshade.Source, RequireSurfaceOutput(surfaceOutput)),
                    hillshade.AzimuthDegrees,
                    hillshade.AltitudeDegrees,
                    hillshade.ZFactor,
                    cancellationToken).ConfigureAwait(false),
            },
            PostgisZonalStatisticsBinding zonal => new PostgisSurfaceZonalPrimitiveResult
            {
                ZonalStatistics = await rasterStore.ComputeZonalStatisticsAsync(
                    zonal.Source.LayerId,
                    zonal.Source.RasterId,
                    zonal.ZonesLayerId,
                    zonal.Band,
                    zonal.Statistics,
                    cancellationToken).ConfigureAwait(false),
            },
            _ => throw new ArgumentException(
                "The PostGIS primitive dispatcher received an unsupported binding.",
                nameof(binding)),
        };
    }

    private static SurfaceAnalysisRequest CreateSurfaceRequest(
        PostgisRasterSourceDescriptor source,
        PostgisPreparedSurfaceOutput output) => new()
        {
            SourceLayerId = source.LayerId,
            SourceRasterId = source.RasterId,
            OutputLayerId = output.LayerId,
            OutputName = output.Name,
        };

    private static PostgisPreparedSurfaceOutput RequireSurfaceOutput(
        PostgisPreparedSurfaceOutput? output) => output
        ?? throw new InvalidOperationException(
            "Surface execution requires an execution-owned prepared output target.");
}

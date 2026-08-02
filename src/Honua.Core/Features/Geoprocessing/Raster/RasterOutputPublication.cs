// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Raster;

/// <summary>Terminal job state used to gate output visibility.</summary>
public enum RasterOutputCompletionState
{
    /// <summary>The producing job succeeded and its outputs may be published.</summary>
    Succeeded,

    /// <summary>The producing job failed; staged outputs must remain invisible.</summary>
    Failed,

    /// <summary>The producing job was cancelled; staged outputs must remain invisible.</summary>
    Cancelled
}

/// <summary>Optional durable target created by the registration transaction.</summary>
public enum RasterOutputRegistrationKind
{
    /// <summary>Register a stable result artifact without creating another raster target.</summary>
    ResultArtifact,

    /// <summary>Register the immutable COG or Zarr object in the raster catalog.</summary>
    CatalogObject,

    /// <summary>Import and register the staged raster in PostGIS.</summary>
    PostgisRaster
}

/// <summary>Stable logical registration target, never a SQL identifier, URL, or connection string.</summary>
/// <param name="Kind">Registration operation performed transactionally.</param>
/// <param name="TargetReference">Operator-defined logical target registration.</param>
public sealed record RasterOutputRegistrationTarget(
    RasterOutputRegistrationKind Kind,
    string TargetReference);

/// <summary>Request to make one staged output visible.</summary>
public sealed record RasterOutputPublicationRequest
{
    /// <summary>Metadata-only staged output.</summary>
    public required StagedRasterOutputDescriptor Stage { get; init; }

    /// <summary>Terminal state of the producing job.</summary>
    public required RasterOutputCompletionState CompletionState { get; init; }

    /// <summary>Logical catalog, PostGIS, or result-artifact target.</summary>
    public required RasterOutputRegistrationTarget RegistrationTarget { get; init; }

    /// <summary>Stable publication timestamp reused by reconciler replays.</summary>
    public required DateTimeOffset PublishedAt { get; init; }

    /// <summary>Expiry governed by the existing result-retention policy.</summary>
    public required DateTimeOffset RetainUntil { get; init; }
}

/// <summary>Physical state of an object known to the publication subsystem.</summary>
public enum RasterStoredObjectState
{
    /// <summary>Attempt-scoped bytes not visible as a successful result.</summary>
    Staged,

    /// <summary>Bytes moved to their immutable stable key but not necessarily registered.</summary>
    Published
}

/// <summary>Verified object metadata returned by an output object store.</summary>
public sealed record RasterStoredObject
{
    /// <summary>Logical store registration.</summary>
    public required string StoreReference { get; init; }

    /// <summary>Relative object key.</summary>
    public required string ObjectKey { get; init; }

    /// <summary>Immutable provider or content-derived version.</summary>
    public required string ObjectVersion { get; init; }

    /// <summary>Verified encoded size, media type, and strong checksum.</summary>
    public required RasterContentIdentity Content { get; init; }

    /// <summary>Staging or stable publication state.</summary>
    public required RasterStoredObjectState State { get; init; }

    /// <summary>Last mutation timestamp used by bounded orphan reconciliation.</summary>
    public required DateTimeOffset LastModifiedAt { get; init; }
}

/// <summary>Idempotent same-store promotion request.</summary>
public sealed record RasterObjectPublicationRequest
{
    /// <summary>Verified metadata for the staged source.</summary>
    public required StagedRasterOutputDescriptor Stage { get; init; }

    /// <summary>Deterministic immutable destination key.</summary>
    public required string DestinationObjectKey { get; init; }

    /// <summary>Stable publication timestamp.</summary>
    public required DateTimeOffset PublishedAt { get; init; }
}

/// <summary>
/// Object-store operations used by worker-side output publication. Implementations must stream
/// content, validate declared size/media/checksum, isolate attempts, and make same-store promotion
/// atomic. A repeated promotion must return the existing object only when its identity matches.
/// </summary>
public interface IRasterOutputObjectStore
{
    /// <summary>Streams bytes to an attempt-scoped staging key and verifies their identity.</summary>
    Task<RasterStoredObject> StageAsync(
        StagedRasterOutputDescriptor descriptor,
        Stream content,
        CancellationToken cancellationToken = default);

    /// <summary>Returns verified metadata for an exact stable or staged object.</summary>
    Task<RasterStoredObject?> InspectAsync(
        string storeReference,
        string objectKey,
        CancellationToken cancellationToken = default);

    /// <summary>Atomically promotes staged bytes to a deterministic immutable key.</summary>
    Task<RasterStoredObject> PublishAsync(
        RasterObjectPublicationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Lists a bounded set of staged or published objects older than a cutoff.</summary>
    IAsyncEnumerable<RasterStoredObject> ListExpiredAsync(
        DateTimeOffset olderThan,
        int maximumCount,
        CancellationToken cancellationToken = default);

    /// <summary>Idempotently removes an exact staged or published object.</summary>
    Task DeleteAsync(
        string storeReference,
        string objectKey,
        CancellationToken cancellationToken = default);
}

/// <summary>Atomic target-registration command with deterministic replay identity.</summary>
public sealed record RasterOutputRegistrationCommand
{
    /// <summary>Deterministic idempotency key unique to job, logical output, and content.</summary>
    public required string IdempotencyKey { get; init; }

    /// <summary>Immutable object descriptor available to the registration transaction.</summary>
    public required ObjectStoreRasterOutputDescriptor PublishedObject { get; init; }

    /// <summary>Logical result-artifact, catalog-object, or PostGIS target.</summary>
    public required RasterOutputRegistrationTarget Target { get; init; }
}

/// <summary>Result of an atomic output registration.</summary>
/// <param name="Output">Visible object-store or PostGIS output descriptor.</param>
/// <param name="AlreadyRegistered">Whether an identical prior command was replayed.</param>
public sealed record RasterOutputRegistrationResult(
    RasterOutputDescriptor Output,
    bool AlreadyRegistered);

/// <summary>
/// Transactional visibility seam for raster outputs. Provider implementations must use a unique
/// idempotency key and one database transaction for the optional catalog/PostGIS target plus the
/// durable visible-result row. A failure must not leave a row observable as a successful result.
/// </summary>
public interface IRasterOutputRegistry
{
    /// <summary>Creates or replays an atomic, idempotent output registration.</summary>
    Task<RasterOutputRegistrationResult> RegisterAtomicallyAsync(
        RasterOutputRegistrationCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>Checks whether an immutable published object is referenced by a visible result.</summary>
    Task<bool> IsVisibleAsync(
        string storeReference,
        string objectKey,
        CancellationToken cancellationToken = default);
}

/// <summary>Externally visible result of publication.</summary>
public enum RasterOutputPublicationState
{
    /// <summary>The registry transaction completed and the output is visible.</summary>
    Published,

    /// <summary>The job did not succeed; the output is hidden and cleanup was attempted.</summary>
    Suppressed
}

/// <summary>Result of a publication attempt.</summary>
public sealed record RasterOutputPublicationResult
{
    /// <summary>Visible or suppressed state.</summary>
    public required RasterOutputPublicationState State { get; init; }

    /// <summary>Visible descriptor only after atomic registration succeeds.</summary>
    public RasterOutputDescriptor? Output { get; init; }

    /// <summary>Whether cleanup must be completed by the orphan reconciler.</summary>
    public bool CleanupDeferred { get; init; }
}

/// <summary>Summary of a bounded orphan sweep.</summary>
/// <param name="Inspected">Candidates inspected.</param>
/// <param name="Deleted">Unregistered candidates deleted.</param>
/// <param name="RetainedVisible">Visible published objects retained.</param>
public sealed record RasterOutputOrphanSweepResult(int Inspected, int Deleted, int RetainedVisible);

/// <summary>Coordinates retry-safe promotion, atomic registration, and bounded orphan cleanup.</summary>
public sealed class RasterOutputPublisher
{
    private readonly IRasterOutputObjectStore _objectStore;
    private readonly IRasterOutputRegistry _registry;

    /// <summary>Creates a raster output publisher.</summary>
    public RasterOutputPublisher(IRasterOutputObjectStore objectStore, IRasterOutputRegistry registry)
    {
        _objectStore = objectStore ?? throw new ArgumentNullException(nameof(objectStore));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    /// <summary>
    /// Promotes and registers a successful output. Failed or cancelled outputs never reach the
    /// registry. Registration exceptions are deliberately propagated so no caller can construct
    /// an incomplete successful result; replay uses the same object and idempotency key.
    /// </summary>
    public async Task<RasterOutputPublicationResult> PublishAsync(
        RasterOutputPublicationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validation = RasterOutputDescriptorValidator.Validate(request.Stage);
        if (!validation.IsValid)
        {
            throw new ArgumentException(
                $"Staged raster output is invalid: {string.Join(", ", validation.Errors.Select(error => error.Code))}.",
                nameof(request));
        }

        ValidatePublicationMetadata(request);
        if (request.CompletionState != RasterOutputCompletionState.Succeeded)
        {
            var cleanupDeferred = cancellationToken.IsCancellationRequested;
            try
            {
                if (!cleanupDeferred)
                {
                    await _objectStore.DeleteAsync(
                        request.Stage.StoreReference,
                        request.Stage.ObjectKey,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                cleanupDeferred = true;
            }

            return new RasterOutputPublicationResult
            {
                State = RasterOutputPublicationState.Suppressed,
                CleanupDeferred = cleanupDeferred
            };
        }

        cancellationToken.ThrowIfCancellationRequested();

        var checksum = request.Stage.Content.Checksum!;
        var artifactId = RasterOutputIdentity.CreateArtifactId(
            request.Stage.JobId,
            request.Stage.OutputName,
            checksum);
        var destinationKey = BuildPublishedObjectKey(artifactId, request.Stage.Encoding);
        var stored = await _objectStore.PublishAsync(
            new RasterObjectPublicationRequest
            {
                Stage = request.Stage,
                DestinationObjectKey = destinationKey,
                PublishedAt = request.PublishedAt
            },
            cancellationToken).ConfigureAwait(false);
        EnsureStoredIdentity(request.Stage, stored, destinationKey);

        var objectOutput = new ObjectStoreRasterOutputDescriptor
        {
            ArtifactId = artifactId,
            OutputName = request.Stage.OutputName,
            StoreReference = stored.StoreReference,
            ObjectKey = stored.ObjectKey,
            ObjectVersion = stored.ObjectVersion,
            Encoding = request.Stage.Encoding,
            Content = stored.Content,
            Grid = request.Stage.Grid,
            Engine = request.Stage.Engine,
            Lineage = request.Stage.Lineage,
            Retention = new RasterOutputRetention(request.PublishedAt, request.RetainUntil)
        };
        var registration = await _registry.RegisterAtomicallyAsync(
            new RasterOutputRegistrationCommand
            {
                IdempotencyKey = artifactId,
                PublishedObject = objectOutput,
                Target = request.RegistrationTarget
            },
            cancellationToken).ConfigureAwait(false);
        EnsureRegisteredIdentity(objectOutput, registration.Output, request.RegistrationTarget.Kind);

        return new RasterOutputPublicationResult
        {
            State = RasterOutputPublicationState.Published,
            Output = registration.Output
        };
    }

    /// <summary>Deletes expired staged and unregistered published objects while retaining visible outputs.</summary>
    public async Task<RasterOutputOrphanSweepResult> SweepOrphansAsync(
        DateTimeOffset olderThan,
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCount);
        var inspected = 0;
        var deleted = 0;
        var retained = 0;

        await foreach (var candidate in _objectStore.ListExpiredAsync(
            olderThan,
            maximumCount,
            cancellationToken).ConfigureAwait(false))
        {
            inspected++;
            if (candidate.State == RasterStoredObjectState.Published
                && await _registry.IsVisibleAsync(
                    candidate.StoreReference,
                    candidate.ObjectKey,
                    cancellationToken).ConfigureAwait(false))
            {
                retained++;
                continue;
            }

            await _objectStore.DeleteAsync(
                candidate.StoreReference,
                candidate.ObjectKey,
                cancellationToken).ConfigureAwait(false);
            deleted++;
        }

        return new RasterOutputOrphanSweepResult(inspected, deleted, retained);
    }

    private static string BuildPublishedObjectKey(string artifactId, RasterOutputEncoding encoding)
    {
        var extension = encoding switch
        {
            RasterOutputEncoding.CloudOptimizedGeoTiff => ".tif",
            RasterOutputEncoding.Zarr => ".zarr",
            _ => throw new InvalidOperationException("Only COG and Zarr staging encodings can be object-published.")
        };
        return $"raster/published/{artifactId.AsSpan(5, 2)}/{artifactId}{extension}";
    }

    private static void ValidatePublicationMetadata(RasterOutputPublicationRequest request)
    {
        if (request.RetainUntil <= request.PublishedAt)
        {
            throw new ArgumentException("Raster output retention must end after publication.", nameof(request));
        }

        var target = request.RegistrationTarget;
        if (target is null || !Enum.IsDefined(target.Kind)
            || !RasterOutputWorkerContract.IsLogicalStoreReference(target.TargetReference))
        {
            throw new ArgumentException(
                "Raster output registration target must be a bounded logical reference without URL or credential syntax.",
                nameof(request));
        }
    }

    private static void EnsureStoredIdentity(
        StagedRasterOutputDescriptor stage,
        RasterStoredObject stored,
        string destinationKey)
    {
        if (stored.State != RasterStoredObjectState.Published
            || !string.Equals(stored.StoreReference, stage.StoreReference, StringComparison.Ordinal)
            || !string.Equals(stored.ObjectKey, destinationKey, StringComparison.Ordinal)
            || stored.Content != stage.Content)
        {
            throw new InvalidOperationException("Object store returned a published raster with a different identity.");
        }
    }

    private static void EnsureRegisteredIdentity(
        ObjectStoreRasterOutputDescriptor expected,
        RasterOutputDescriptor actual,
        RasterOutputRegistrationKind registrationKind)
    {
        if (!string.Equals(expected.ArtifactId, actual.ArtifactId, StringComparison.Ordinal)
            || !string.Equals(expected.OutputName, actual.OutputName, StringComparison.Ordinal)
            || expected.Content != actual.Content || !GridEquals(expected.Grid, actual.Grid)
            || expected.Engine != actual.Engine || !LineageEquals(expected.Lineage, actual.Lineage)
            || expected.Retention != actual.Retention)
        {
            throw new InvalidOperationException("Registry returned a raster output with a different durable identity.");
        }

        var validation = RasterOutputDescriptorValidator.Validate(actual);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException("Registry returned an invalid raster output descriptor.");
        }

        if (registrationKind == RasterOutputRegistrationKind.PostgisRaster
            ? actual is not PostgisRasterOutputDescriptor
            : actual is not ObjectStoreRasterOutputDescriptor)
        {
            throw new InvalidOperationException(
                "Registry returned a raster output type that does not match the registration target.");
        }
    }

    private static bool GridEquals(RasterGridMetadata expected, RasterGridMetadata? actual) =>
        actual is not null
        && string.Equals(expected.Crs, actual.Crs, StringComparison.Ordinal)
        && expected.Width == actual.Width
        && expected.Height == actual.Height
        && expected.BandCount == actual.BandCount
        && actual.GeoTransform is not null
        && expected.GeoTransform.SequenceEqual(actual.GeoTransform);

    private static bool LineageEquals(RasterOutputLineage expected, RasterOutputLineage? actual) =>
        actual is not null
        && string.Equals(expected.JobId, actual.JobId, StringComparison.Ordinal)
        && expected.Attempt == actual.Attempt
        && string.Equals(expected.ProcessId, actual.ProcessId, StringComparison.Ordinal)
        && actual.SourceArtifactIds is not null
        && expected.SourceArtifactIds.SequenceEqual(actual.SourceArtifactIds, StringComparer.Ordinal);
}

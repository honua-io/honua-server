// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.Capacity;

/// <summary>
/// Provider-neutral estimate of the work performed by one synchronous raster request.
/// Each field is budgeted independently; source I/O and database work are not proxies
/// for the memory required to materialize a web response.
/// </summary>
public readonly record struct RasterCapacityWork(
    long WebOutputCells,
    long WebOutputBytes,
    long ObjectRangeRequests,
    long ObjectRangeBytes,
    long PostGisWorkUnits)
{
    /// <summary>
    /// A request that consumes only a concurrency slot.
    /// </summary>
    public static RasterCapacityWork Empty => default;
}

/// <summary>
/// Independent ceilings for synchronous raster work.
/// </summary>
public readonly record struct RasterCapacityBudget(
    long MaxWebOutputCells,
    long MaxWebOutputBytes,
    long MaxObjectRangeRequests,
    long MaxObjectRangeBytes,
    long MaxPostGisWorkUnits)
{
    /// <summary>
    /// Finds the first independently budgeted dimension exceeded by <paramref name="work"/>.
    /// </summary>
    public bool TryFindExceededDimension(
        RasterCapacityWork work,
        out RasterCapacityDimension dimension,
        out long requested,
        out long limit)
    {
        ValidateNonNegative(work);
        ValidatePositiveBudget(this);

        if (work.WebOutputCells > MaxWebOutputCells)
        {
            return Exceeded(RasterCapacityDimension.WebOutputCells, work.WebOutputCells, MaxWebOutputCells,
                out dimension, out requested, out limit);
        }

        if (work.WebOutputBytes > MaxWebOutputBytes)
        {
            return Exceeded(RasterCapacityDimension.WebOutputBytes, work.WebOutputBytes, MaxWebOutputBytes,
                out dimension, out requested, out limit);
        }

        if (work.ObjectRangeRequests > MaxObjectRangeRequests)
        {
            return Exceeded(RasterCapacityDimension.ObjectRangeRequests, work.ObjectRangeRequests, MaxObjectRangeRequests,
                out dimension, out requested, out limit);
        }

        if (work.ObjectRangeBytes > MaxObjectRangeBytes)
        {
            return Exceeded(RasterCapacityDimension.ObjectRangeBytes, work.ObjectRangeBytes, MaxObjectRangeBytes,
                out dimension, out requested, out limit);
        }

        if (work.PostGisWorkUnits > MaxPostGisWorkUnits)
        {
            return Exceeded(RasterCapacityDimension.PostGisWorkUnits, work.PostGisWorkUnits, MaxPostGisWorkUnits,
                out dimension, out requested, out limit);
        }

        dimension = RasterCapacityDimension.None;
        requested = 0;
        limit = 0;
        return false;
    }

    private static bool Exceeded(
        RasterCapacityDimension exceededDimension,
        long exceededRequested,
        long exceededLimit,
        out RasterCapacityDimension dimension,
        out long requested,
        out long limit)
    {
        dimension = exceededDimension;
        requested = exceededRequested;
        limit = exceededLimit;
        return true;
    }

    private static void ValidateNonNegative(RasterCapacityWork work)
    {
        if (work.WebOutputCells < 0 || work.WebOutputBytes < 0 ||
            work.ObjectRangeRequests < 0 || work.ObjectRangeBytes < 0 ||
            work.PostGisWorkUnits < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(work), "Raster work estimates cannot be negative.");
        }
    }

    private static void ValidatePositiveBudget(RasterCapacityBudget budget)
    {
        if (budget.MaxWebOutputCells <= 0 || budget.MaxWebOutputBytes <= 0 ||
            budget.MaxObjectRangeRequests <= 0 || budget.MaxObjectRangeBytes <= 0 ||
            budget.MaxPostGisWorkUnits <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(budget), "Raster capacity budget limits must be positive.");
        }
    }
}

/// <summary>
/// The independently controlled capacity dimension that denied a request.
/// </summary>
public enum RasterCapacityDimension
{
    /// <summary>No dimension denied the request.</summary>
    None = 0,

    /// <summary>Number of web response cells.</summary>
    WebOutputCells,

    /// <summary>Estimated managed bytes required to materialize the web response.</summary>
    WebOutputBytes,

    /// <summary>Number of object-store range requests.</summary>
    ObjectRangeRequests,

    /// <summary>Conservative aggregate bytes addressed by object-store range requests.</summary>
    ObjectRangeBytes,

    /// <summary>Provider-estimated PostGIS raster work units.</summary>
    PostGisWorkUnits,

    /// <summary>Concurrent synchronous work across the serving instance.</summary>
    GlobalConcurrency,

    /// <summary>Concurrent synchronous work in one tenant fairness partition.</summary>
    TenantConcurrency,
}

/// <summary>
/// Classification of a capacity denial.
/// </summary>
public enum RasterCapacityDenialKind
{
    /// <summary>The estimated request work exceeds a configured static budget.</summary>
    WorkLimitExceeded,

    /// <summary>The serving instance has no global synchronous raster slot available.</summary>
    GlobalConcurrencyExceeded,

    /// <summary>The tenant fairness partition has no synchronous raster slot available.</summary>
    TenantConcurrencyExceeded,
}

/// <summary>
/// Action a protocol can recommend when synchronous work is refused.
/// </summary>
public enum RasterCapacityOverflowAction
{
    /// <summary>Reduce the request or refuse it.</summary>
    ReduceOrReject,

    /// <summary>Submit the work to a durable geoprocessing job.</summary>
    SubmitDurableJob,
}

/// <summary>
/// Request for one synchronous raster capacity lease.
/// </summary>
/// <param name="Operation">Stable protocol-neutral operation identifier.</param>
/// <param name="TenantPartition">Tenant or anonymous fairness partition.</param>
/// <param name="Work">Provider-neutral work estimate.</param>
/// <param name="OverflowAction">Action recommended when synchronous admission is denied.</param>
public sealed record RasterCapacityRequest(
    string Operation,
    string TenantPartition,
    RasterCapacityWork Work,
    RasterCapacityOverflowAction OverflowAction);

/// <summary>
/// Lease holding global and per-tenant synchronous raster concurrency.
/// </summary>
public interface IRasterCapacityLease : IAsyncDisposable
{
}

/// <summary>
/// Outcome of synchronous raster admission.
/// </summary>
public sealed record RasterCapacityAdmissionResult
{
    private RasterCapacityAdmissionResult(
        IRasterCapacityLease? lease,
        RasterCapacityDenialKind? denialKind,
        RasterCapacityDimension dimension,
        long requested,
        long limit,
        RasterCapacityOverflowAction overflowAction,
        int? retryAfterSeconds)
    {
        Lease = lease;
        DenialKind = denialKind;
        Dimension = dimension;
        Requested = requested;
        Limit = limit;
        OverflowAction = overflowAction;
        RetryAfterSeconds = retryAfterSeconds;
    }

    /// <summary>Whether the request was admitted.</summary>
    public bool IsAdmitted => Lease is not null;

    /// <summary>The lease to dispose after synchronous work completes.</summary>
    public IRasterCapacityLease? Lease { get; }

    /// <summary>The denial classification, or <see langword="null"/> when admitted.</summary>
    public RasterCapacityDenialKind? DenialKind { get; }

    /// <summary>The dimension that denied the request.</summary>
    public RasterCapacityDimension Dimension { get; }

    /// <summary>The estimated amount requested for the denied dimension.</summary>
    public long Requested { get; }

    /// <summary>The configured limit for the denied dimension.</summary>
    public long Limit { get; }

    /// <summary>The protocol action recommended if this request is denied.</summary>
    public RasterCapacityOverflowAction OverflowAction { get; }

    /// <summary>Suggested retry delay for transient concurrency denials.</summary>
    public int? RetryAfterSeconds { get; }

    /// <summary>Creates an admitted result holding <paramref name="lease"/>.</summary>
    public static RasterCapacityAdmissionResult Admitted(IRasterCapacityLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return new RasterCapacityAdmissionResult(
            lease, null, RasterCapacityDimension.None, 0, 0,
            RasterCapacityOverflowAction.ReduceOrReject, null);
    }

    /// <summary>Creates a denied result.</summary>
    public static RasterCapacityAdmissionResult Denied(
        RasterCapacityDenialKind denialKind,
        RasterCapacityDimension dimension,
        long requested,
        long limit,
        RasterCapacityOverflowAction overflowAction,
        int? retryAfterSeconds = null)
        => new(null, denialKind, dimension, requested, limit, overflowAction, retryAfterSeconds);
}

/// <summary>
/// Provider-neutral admission seam for synchronous raster serving. Implementations
/// must evaluate static work before claiming concurrency and return without performing
/// raster allocation, object reads, database SQL, or native-library work.
/// </summary>
public interface IRasterCapacityAdmission
{
    /// <summary>
    /// Attempts to acquire a lease without queueing expensive synchronous work.
    /// </summary>
    ValueTask<RasterCapacityAdmissionResult> TryAcquireAsync(
        RasterCapacityRequest request,
        CancellationToken cancellationToken = default);
}

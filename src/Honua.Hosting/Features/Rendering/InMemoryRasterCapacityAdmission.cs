// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Raster.Capacity;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Rendering;

/// <summary>
/// Per-serving-instance synchronous raster admission. The provider-neutral interface
/// permits a distributed implementation later without changing protocol adapters.
/// </summary>
internal sealed class InMemoryRasterCapacityAdmission : IRasterCapacityAdmission
{
    internal const string AnonymousTenantPartition = "__anonymous__";

    private readonly object _sync = new();
    private readonly RasterCapacityOptions _options;
    private readonly RasterCapacityBudget _budget;
    private readonly Dictionary<string, int> _activeByTenant = new(StringComparer.Ordinal);
    private int _activeRequests;

    public InMemoryRasterCapacityAdmission(IOptions<RasterCapacityOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _budget = new RasterCapacityBudget(
            _options.MaxWebOutputCells,
            _options.MaxWebOutputBytes,
            _options.MaxObjectRangeRequests,
            _options.MaxObjectRangeBytes,
            _options.MaxPostGisWorkUnits);
    }

    /// <inheritdoc />
    public ValueTask<RasterCapacityAdmissionResult> TryAcquireAsync(
        RasterCapacityRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Operation);
        cancellationToken.ThrowIfCancellationRequested();

        // Static work is evaluated before the lock and before a concurrency slot is
        // claimed. Protocols call this seam before raster allocation, object reads, or SQL.
        if (_budget.TryFindExceededDimension(request.Work, out var dimension, out var requested, out var limit))
        {
            return ValueTask.FromResult(RasterCapacityAdmissionResult.Denied(
                RasterCapacityDenialKind.WorkLimitExceeded,
                dimension,
                requested,
                limit,
                request.OverflowAction));
        }

        var tenantPartition = NormalizeTenantPartition(request.TenantPartition);
        lock (_sync)
        {
            if (_activeRequests >= _options.MaxConcurrentRequests)
            {
                return ValueTask.FromResult(RasterCapacityAdmissionResult.Denied(
                    RasterCapacityDenialKind.GlobalConcurrencyExceeded,
                    RasterCapacityDimension.GlobalConcurrency,
                    _activeRequests + 1L,
                    _options.MaxConcurrentRequests,
                    request.OverflowAction,
                    _options.RetryAfterSeconds));
            }

            _activeByTenant.TryGetValue(tenantPartition, out var tenantActive);
            if (tenantActive >= _options.MaxConcurrentRequestsPerTenant)
            {
                return ValueTask.FromResult(RasterCapacityAdmissionResult.Denied(
                    RasterCapacityDenialKind.TenantConcurrencyExceeded,
                    RasterCapacityDimension.TenantConcurrency,
                    tenantActive + 1L,
                    _options.MaxConcurrentRequestsPerTenant,
                    request.OverflowAction,
                    _options.RetryAfterSeconds));
            }

            _activeRequests++;
            _activeByTenant[tenantPartition] = tenantActive + 1;
        }

        return ValueTask.FromResult(RasterCapacityAdmissionResult.Admitted(new Lease(this, tenantPartition)));
    }

    private static string NormalizeTenantPartition(string? tenantPartition)
        => string.IsNullOrWhiteSpace(tenantPartition)
            ? AnonymousTenantPartition
            : tenantPartition.Trim();

    private void Release(string tenantPartition)
    {
        lock (_sync)
        {
            _activeRequests--;
            var tenantActive = _activeByTenant[tenantPartition] - 1;
            if (tenantActive == 0)
            {
                _activeByTenant.Remove(tenantPartition);
            }
            else
            {
                _activeByTenant[tenantPartition] = tenantActive;
            }
        }
    }

    private sealed class Lease(InMemoryRasterCapacityAdmission owner, string tenantPartition) : IRasterCapacityLease
    {
        private InMemoryRasterCapacityAdmission? _owner = owner;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _owner, null)?.Release(tenantPartition);
            return ValueTask.CompletedTask;
        }
    }
}

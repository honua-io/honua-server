// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Features.Geoprocessing.Raster;
using Microsoft.Extensions.Options;

namespace Honua.Postgres.Features.Raster;

internal sealed class PostgisRasterAdmissionController : IDisposable
{
    private readonly object _tenantGateLock = new();
    private readonly Dictionary<string, TenantGate> _tenantGates = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _globalGate;
    private readonly PostgisRasterExecutionOptions _options;
    private bool _disposed;
    private bool _semaphoresDisposed;

    public PostgisRasterAdmissionController(IOptions<PostgisRasterExecutionOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _globalGate = new SemaphoreSlim(_options.MaxConcurrency, _options.MaxConcurrency);
    }

    public async ValueTask<IAsyncDisposable> AcquireAsync(
        RasterProviderExecutionRequest request,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateRequest(request);

        var tenantGate = RetainTenantGate(request.TenantId);
        var stopwatch = Stopwatch.StartNew();
        var tenantAcquired = false;
        var globalAcquired = false;
        try
        {
            tenantAcquired = await tenantGate.Semaphore.WaitAsync(
                _options.QueueTimeout,
                cancellationToken).ConfigureAwait(false);
            if (!tenantAcquired)
            {
                throw PostgisRasterGovernanceException.AdmissionTimeout();
            }

            var remaining = _options.QueueTimeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero ||
                !await _globalGate.WaitAsync(remaining, cancellationToken).ConfigureAwait(false))
            {
                throw PostgisRasterGovernanceException.AdmissionTimeout();
            }

            globalAcquired = true;
            ThrowIfDisposed();
            return new AdmissionLease(this, request.TenantId, tenantGate);
        }
        catch
        {
            if (globalAcquired)
            {
                _globalGate.Release();
            }

            if (tenantAcquired)
            {
                tenantGate.Semaphore.Release();
            }

            ReleaseTenantGateReference(request.TenantId, tenantGate);
            throw;
        }
    }

    public void Dispose()
    {
        lock (_tenantGateLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_tenantGates.Count == 0)
            {
                DisposeGlobalGate();
            }
        }
    }

    private void ValidateRequest(RasterProviderExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.TenantId) || request.TenantId.Length > 256)
        {
            throw PostgisRasterGovernanceException.InvalidTenant();
        }

        if (!request.Parameters.TryGetValue(RasterProviderExecutionParameterKeys.TenantId, out var pinnedTenantId) ||
            !string.Equals(request.TenantId, pinnedTenantId, StringComparison.Ordinal))
        {
            throw PostgisRasterGovernanceException.TenantMismatch();
        }

        if (string.IsNullOrWhiteSpace(request.OperationId) || request.OperationId.Length > 256 ||
            request.Attempt <= 0)
        {
            throw PostgisRasterGovernanceException.InvalidRequest("attempt identity");
        }

        if (request.Decision.Engine != RasterEngine.Postgis ||
            request.Decision.Placement != RasterExecutionPlacement.DurablePostgis ||
            !string.Equals(request.Decision.ProviderId, "postgis", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(request.Decision.ProviderPolicyVersion))
        {
            throw PostgisRasterGovernanceException.InvalidRequest("provider route");
        }

        var cost = request.Decision.Cost;
        if (cost.Engine != RasterEngine.Postgis ||
            !string.Equals(cost.ProcessId, request.Decision.ProcessId, StringComparison.Ordinal))
        {
            throw PostgisRasterGovernanceException.InvalidCost("process identity");
        }

        if (cost.UsesConservativeValues || HasInvalidCost(cost))
        {
            throw PostgisRasterGovernanceException.UnknownCost();
        }

        var limits = ResolveWorkLimits(request.TenantId);
        if (cost.SourceCount > limits.MaxSourceCount)
        {
            throw PostgisRasterGovernanceException.WorkLimitExceeded("source-count");
        }

        if (cost.BandCount > limits.MaxBandCount)
        {
            throw PostgisRasterGovernanceException.WorkLimitExceeded("band-count");
        }

        if (cost.ZoneCount > limits.MaxZoneCount)
        {
            throw PostgisRasterGovernanceException.WorkLimitExceeded("zone-count");
        }

        if (cost.InputPixels > limits.MaxInputPixels)
        {
            throw PostgisRasterGovernanceException.WorkLimitExceeded("input-pixels");
        }

        if (cost.OutputPixels > limits.MaxOutputPixels)
        {
            throw PostgisRasterGovernanceException.WorkLimitExceeded("output-pixels");
        }

        if (cost.DecodedBytes > limits.MaxDecodedBytes)
        {
            throw PostgisRasterGovernanceException.WorkLimitExceeded("decoded-bytes");
        }

        if (cost.ExpectedScratchBytes > limits.MaxScratchBytes)
        {
            throw PostgisRasterGovernanceException.WorkLimitExceeded("scratch-bytes");
        }

        if (cost.ExpectedDatabaseWork > limits.MaxDatabaseWork)
        {
            throw PostgisRasterGovernanceException.WorkLimitExceeded("database-work");
        }
    }

    private static bool HasInvalidCost(RasterCostEstimate cost) =>
        cost.SourceCount < 0 || cost.BandCount < 0 || cost.ZoneCount < 0 ||
        cost.InputPixels < 0 || cost.OutputPixels < 0 || cost.DecodedBytes < 0 ||
        cost.ExpectedScratchBytes < 0 || cost.ExpectedDatabaseWork < 0;

    private PostgisRasterTenantPolicy? ResolveTenantPolicy(string tenantId) =>
        _options.Tenants.TryGetValue(tenantId, out var policy)
            ? policy
            : null;

    private EffectiveWorkLimits ResolveWorkLimits(string tenantId)
    {
        var tenant = ResolveTenantPolicy(tenantId)?.WorkLimits;
        var global = _options.WorkLimits;
        return new EffectiveWorkLimits(
            tenant?.MaxSourceCount ?? global.MaxSourceCount,
            tenant?.MaxBandCount ?? global.MaxBandCount,
            tenant?.MaxZoneCount ?? global.MaxZoneCount,
            tenant?.MaxInputPixels ?? global.MaxInputPixels,
            tenant?.MaxOutputPixels ?? global.MaxOutputPixels,
            tenant?.MaxDecodedBytes ?? global.MaxDecodedBytes,
            tenant?.MaxScratchBytes ?? global.MaxScratchBytes,
            tenant?.MaxDatabaseWork ?? global.MaxDatabaseWork);
    }

    private TenantGate RetainTenantGate(string tenantId)
    {
        lock (_tenantGateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_tenantGates.TryGetValue(tenantId, out var gate))
            {
                var limit = ResolveTenantPolicy(tenantId)?.MaxConcurrency ??
                    _options.MaxConcurrencyPerTenant;
                gate = new TenantGate(limit);
                _tenantGates.Add(tenantId, gate);
            }

            gate.ReferenceCount++;
            return gate;
        }
    }

    private void ThrowIfDisposed()
    {
        lock (_tenantGateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }
    }

    private void ReleaseTenantGateReference(string tenantId, TenantGate gate)
    {
        lock (_tenantGateLock)
        {
            gate.ReferenceCount--;
            if (gate.ReferenceCount == 0 && _tenantGates.Remove(tenantId, out var removed))
            {
                removed.Semaphore.Dispose();
            }

            if (_disposed && _tenantGates.Count == 0)
            {
                DisposeGlobalGate();
            }
        }
    }

    private void DisposeGlobalGate()
    {
        if (!_semaphoresDisposed)
        {
            _globalGate.Dispose();
            _semaphoresDisposed = true;
        }
    }

    private sealed class TenantGate(int limit)
    {
        public SemaphoreSlim Semaphore { get; } = new(limit, limit);

        public int ReferenceCount { get; set; }
    }

    private readonly record struct EffectiveWorkLimits(
        long MaxSourceCount,
        long MaxBandCount,
        long MaxZoneCount,
        long MaxInputPixels,
        long MaxOutputPixels,
        long MaxDecodedBytes,
        long MaxScratchBytes,
        long MaxDatabaseWork);

    private sealed class AdmissionLease(
        PostgisRasterAdmissionController owner,
        string tenantId,
        TenantGate tenantGate) : IAsyncDisposable
    {
        private PostgisRasterAdmissionController? _owner = owner;

        public ValueTask DisposeAsync()
        {
            var currentOwner = Interlocked.Exchange(ref _owner, null);
            if (currentOwner is not null)
            {
                currentOwner._globalGate.Release();
                tenantGate.Semaphore.Release();
                currentOwner.ReleaseTenantGateReference(tenantId, tenantGate);
            }

            return ValueTask.CompletedTask;
        }
    }
}

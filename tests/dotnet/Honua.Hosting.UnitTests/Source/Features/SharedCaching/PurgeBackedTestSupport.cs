// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Coordination;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.SharedCaching;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Hosting.UnitTests.Features.SharedCaching;

// Fixtures and doubles shared by the purge-backed lifetime, reliability and delivery tests.
internal static class PurgeBackedFixtures
{
    internal static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "the expected outcome did not happen in time");
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }
    }

    internal static SharedCacheOptions Options(
        string? tagHeader = "x-cache-tag",
        int monthlyLimit = 1000,
        string[]? layers = null,
        Action<SharedCachePurgeBacked>? configure = null)
    {
        var options = new SharedCacheOptions
        {
            CacheTagHeader = tagHeader,
            PurgeBudget = new CdnPurgeBudgetOptions { MonthlyEditSubmissions = monthlyLimit },
        };
        options.PurgeBacked.Layers.AddRange(layers ?? ["parcels/3"]);
        configure?.Invoke(options.PurgeBacked);
        return options;
    }

    internal static CdnPurgeReliability Reliability(
        ManualTimeProvider time, SharedCacheOptions options, ICdnInvalidator invalidator, ICdnPurgeBudget? budget)
        => new(invalidator, NullLogger<CdnPurgeReliability>.Instance, new CdnPurgeCoordination
        {
            TimeProvider = time,
            Options = new StaticOptions(options),
            Budget = budget,
        });

    internal static async Task<CdnPurgeReliability> PrimedReliabilityAsync(
        ManualTimeProvider time, SharedCacheOptions options, ICdnInvalidator? invalidator = null, ICdnPurgeBudget? budget = null)
    {
        var reliability = Reliability(time, options, invalidator ?? new CloudFrontLimits(), budget ?? new FixedBudget(0));
        await reliability.ReadSharedSignalsAsync(options.PurgeBudget, CancellationToken.None);
        return reliability;
    }

    internal static DefaultHttpContext Granted(
        ResponseCacheClass cacheClass, SharedCacheOptions options, CdnPurgeReliability reliability, string? layerId = "3")
    {
        var services = new ServiceCollection()
            .AddSingleton<IOptionsMonitor<SharedCacheOptions>>(new StaticOptions(options))
            .AddSingleton(reliability)
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = HttpMethods.Get;
        context.Request.RouteValues["serviceId"] = "parcels";
        if (layerId is not null)
        {
            context.Request.RouteValues["layerId"] = layerId;
        }

        context.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(new SharedCacheClassMetadata(cacheClass)), "declared"));
        ResponseAccessVerdict.Record(context, AccessDecision.Allowed());
        return context;
    }

    internal static OutputCacheContext OutputCacheContext(HttpContext context)
        => new() { HttpContext = context, AllowCacheStorage = true, ResponseExpirationTimeSpan = TimeSpan.FromHours(1) };
}

internal sealed class FixedBudget(int submitted) : ICdnPurgeBudget
{
    public bool Unreadable { get; init; }

    public CdnPurgeBudgetDecision Decision { get; init; } = CdnPurgeBudgetDecision.Admitted;

    public Task<CdnPurgeAdmission> TryAdmitEditAsync(int monthlyLimit, decimal unitCost, CancellationToken cancellationToken = default)
        => Task.FromResult(submitted >= monthlyLimit
            ? new CdnPurgeAdmission(CdnPurgeBudgetDecision.Exceeded, submitted, 0)
            : new CdnPurgeAdmission(CdnPurgeBudgetDecision.Admitted, submitted + 1, 0));

    public Task<CdnPurgeBudgetMonth?> ReadCurrentMonthAsync(CancellationToken cancellationToken = default)
        => Unreadable
            ? throw new InvalidOperationException("budget store down")
            : Task.FromResult<CdnPurgeBudgetMonth?>(new CdnPurgeBudgetMonth(submitted, 0, Decision));
}

internal sealed class FlakyInvalidator(int failures) : ICdnInvalidator
{
    private readonly Lock _gate = new();
    private readonly TaskCompletionSource _submitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<string[]> _sent = [];
    private TaskCompletionSource _attempted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Attempts
    {
        get
        {
            lock (_gate)
            {
                return _sent.Count;
            }
        }
    }

    public IReadOnlyList<string[]> Sent
    {
        get
        {
            lock (_gate)
            {
                return [.. _sent];
            }
        }
    }

    public Task Submitted => _submitted.Task;

    public async Task AttemptsReach(int count)
    {
        while (true)
        {
            Task attempted;
            lock (_gate)
            {
                if (_sent.Count >= count)
                {
                    return;
                }

                attempted = _attempted.Task;
            }

            await attempted.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    public Task<CdnInvalidationResult> InvalidateAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default)
    {
        bool fail;
        TaskCompletionSource attempted;
        lock (_gate)
        {
            _sent.Add([.. paths]);
            fail = _sent.Count <= failures;
            attempted = _attempted;
            _attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        attempted.TrySetResult();
        if (fail)
        {
            return Task.FromResult(new CdnInvalidationResult(CdnInvalidationOutcome.Failed, "refused"));
        }

        _submitted.TrySetResult();
        return Task.FromResult(new CdnInvalidationResult(CdnInvalidationOutcome.Submitted, "I1"));
    }
}

internal sealed class ReleasableLeases(TimeProvider clock) : ILeaseStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (string Owner, DateTimeOffset ExpiresAt)> _leases = new(StringComparer.Ordinal);
    private TaskCompletionSource _acquired = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _renewed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _renewals;

    public async Task RenewedAtLeast(int count)
    {
        while (true)
        {
            Task renewed;
            lock (_gate)
            {
                if (_renewals >= count)
                {
                    return;
                }

                renewed = _renewed.Task;
            }

            await renewed.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    public async Task Held(string key)
    {
        while (true)
        {
            Task acquired;
            lock (_gate)
            {
                if (_leases.TryGetValue(key, out var lease) && lease.ExpiresAt > clock.GetUtcNow())
                {
                    return;
                }

                acquired = _acquired.Task;
            }

            await acquired.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    public Task<bool> TryAcquireAsync(LeaseClaim claim, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        TaskCompletionSource acquired;
        lock (_gate)
        {
            if (_leases.TryGetValue(claim.LeaseKey, out var lease) && lease.ExpiresAt > clock.GetUtcNow())
            {
                return Task.FromResult(false);
            }

            _leases[claim.LeaseKey] = (claim.OwnerId, clock.GetUtcNow() + duration);
            acquired = _acquired;
            _acquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        acquired.TrySetResult();
        return Task.FromResult(true);
    }

    public Task<bool> RenewAsync(LeaseClaim claim, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_leases.TryGetValue(claim.LeaseKey, out var lease) && lease.Owner == claim.OwnerId && lease.ExpiresAt > clock.GetUtcNow())
            {
                _leases[claim.LeaseKey] = (claim.OwnerId, clock.GetUtcNow() + duration);
                _renewals++;
                var renewed = _renewed;
                _renewed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                renewed.TrySetResult();
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }
    }

    public Task ReleaseAsync(LeaseClaim claim, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_leases.TryGetValue(claim.LeaseKey, out var lease) && lease.Owner == claim.OwnerId)
            {
                _leases.Remove(claim.LeaseKey);
            }
        }

        return Task.CompletedTask;
    }
}

// Reports an unspent month, optionally only once the test lets the read finish.
internal sealed class GatedBudget : ICdnPurgeBudget
{
    private TaskCompletionSource _gate = CompletedGate();

    public void Hold() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Release() => _gate.TrySetResult();

    public Task<CdnPurgeAdmission> TryAdmitEditAsync(int monthlyLimit, decimal unitCost, CancellationToken cancellationToken = default)
        => Task.FromResult(new CdnPurgeAdmission(CdnPurgeBudgetDecision.Admitted, 1, 0));

    public async Task<CdnPurgeBudgetMonth?> ReadCurrentMonthAsync(CancellationToken cancellationToken = default)
    {
        await _gate.Task.WaitAsync(cancellationToken);
        return new CdnPurgeBudgetMonth(0, 0, CdnPurgeBudgetDecision.Admitted);
    }

    private static TaskCompletionSource CompletedGate()
    {
        var gate = new TaskCompletionSource();
        gate.SetResult();
        return gate;
    }
}

internal sealed class FailingInvalidator : ICdnInvalidator
{
    public Task<CdnInvalidationResult> InvalidateAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default)
        => Task.FromResult(new CdnInvalidationResult(CdnInvalidationOutcome.Failed, "refused"));
}

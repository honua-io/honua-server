// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Infrastructure.SharedCaching;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Hosting.UnitTests.Features.SharedCaching;

public sealed class CdnPurgeBudgetTests
{
    private readonly ManualTimeProvider _time = new();

    [UnitTest]
    public async Task TwoTasks_ShareOneEditBudget_AndTheSecondDoesNotCallTheCdn()
    {
        var budget = new SharedBudget(limit: 1);
        var firstCdn = new CloudFrontLimits();
        var secondCdn = new CloudFrontLimits();
        var first = Service(firstCdn, budget, monthlyLimit: 1);
        var second = Service(secondCdn, budget, monthlyLimit: 1);

        await first.InvalidateAfterEditAsync(["/tiles/3/*"], CancellationToken.None);
        await second.InvalidateAfterEditAsync(["/tiles/3/*"], CancellationToken.None);

        firstCdn.Accepted.Should().ContainSingle();
        secondCdn.Accepted.Should().BeEmpty();
        budget.ReadCount.Should().Be(1);
    }

    [UnitTest]
    public async Task SpentBudget_StillPurgesAnAdminRequest()
    {
        var budget = new SharedBudget(limit: 1);
        var cdn = new CloudFrontLimits();
        var service = Service(cdn, budget, monthlyLimit: 1);

        await service.InvalidateAfterEditAsync(["/tiles/3/*"], CancellationToken.None);
        await service.InvalidateNowAsync(["/tiles/3/*"], CancellationToken.None);

        cdn.Accepted.Should().HaveCount(2);
        budget.ReadCount.Should().Be(1);
    }

    [UnitTest]
    public async Task ZeroLimit_DoesNotConsultTheBudget()
    {
        var budget = new SharedBudget(limit: 0);
        var cdn = new CloudFrontLimits();
        var service = Service(cdn, budget, monthlyLimit: 0);

        await service.InvalidateAfterEditAsync(["/tiles/3/*"], CancellationToken.None);

        cdn.Accepted.Should().ContainSingle();
        budget.ReadCount.Should().Be(0);
    }

    [UnitTest]
    public async Task UnreadableBudget_StillSendsTheEditPurge()
    {
        var cdn = new CloudFrontLimits();
        var service = Service(cdn, new UnreadableBudget(), monthlyLimit: 1);

        await service.InvalidateAfterEditAsync(["/tiles/3/*"], CancellationToken.None);

        cdn.Accepted.Should().ContainSingle();
    }

    private CdnInvalidationService Service(CloudFrontLimits cdn, ICdnPurgeBudget budget, int monthlyLimit)
        => new(cdn, NullLogger<CdnInvalidationService>.Instance, new CdnPurgeCoordination
        {
            TimeProvider = _time,
            Options = new StaticOptions(new SharedCacheOptions
            {
                PurgeBudget = new CdnPurgeBudgetOptions { MonthlyEditSubmissions = monthlyLimit },
            }),
            Budget = budget,
        });

    private sealed class SharedBudget(int limit) : ICdnPurgeBudget
    {
        private int _count;

        public int ReadCount => _count;

        public Task<CdnPurgeAdmission> TryAdmitEditAsync(
            int monthlyLimit,
            decimal unitCost,
            CancellationToken cancellationToken = default)
        {
            // Match the database statement: a refused purge does not increment the counter.
            lock (this)
            {
                if (_count >= limit)
                {
                    return Task.FromResult(new CdnPurgeAdmission(CdnPurgeBudgetDecision.Exceeded, _count, 0));
                }

                _count++;
                return Task.FromResult(new CdnPurgeAdmission(CdnPurgeBudgetDecision.Admitted, _count, unitCost * _count));
            }
        }

        public Task<CdnPurgeBudgetMonth?> ReadCurrentMonthAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<CdnPurgeBudgetMonth?>(new CdnPurgeBudgetMonth(_count, 0, _count >= limit && limit > 0));
    }

    private sealed class UnreadableBudget : ICdnPurgeBudget
    {
        public Task<CdnPurgeAdmission> TryAdmitEditAsync(
            int monthlyLimit,
            decimal unitCost,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("budget store down");

        public Task<CdnPurgeBudgetMonth?> ReadCurrentMonthAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("budget store down");
    }
}

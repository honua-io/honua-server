// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Security.Admission;
using Honua.TestKit.Attributes;

namespace Honua.Db.Postgres.Tests.Features.Security;

public sealed partial class PostgresAiExecutionAdmissionStoreTests
{
    [IntegrationTheory]
    [InlineData("tokens")]
    [InlineData("money")]
    public async Task MonthlyActor_ConcurrentReservationsAcrossNodes_HardStopAndActorIsolation(string cappedDimension)
    {
        var policy = FinancialPolicy();
        policy = policy with
        {
            Financial = policy.Financial! with
            {
                MonthlyActor = new()
                {
                    Tokens = cappedDimension == "money" ? 1000 : 15,
                    Money = cappedDimension == "money" ? 15 : 1000
                }
            }
        };
        var first = Store(); var second = Store();
        await InitializeAndResumeAsync(first, policy);
        var tasks = Enumerable.Range(0, 12).Select(index => (index % 2 == 0 ? first : second)
            .TryAcquireAsync(FinancialContext(), Guid.NewGuid(), Frozen(policy), default));
        var outcomes = await Task.WhenAll(tasks);
        outcomes.Count(result => result.Granted).Should().Be(1);
        outcomes.Where(result => !result.Granted).Should().OnlyContain(result => result.Code == "financial_monthly_actor_exceeded");
        (await CountAttemptsAsync()).Should().Be(1);
        (await Store().TryAcquireAsync(FinancialContext("another-agent"), Guid.NewGuid(), Frozen(policy), default))
            .Granted.Should().BeTrue("a restarted node must preserve isolation between canonical actors");
    }

    [IntegrationTest]
    public async Task MonthlyActor_UnsettledDebtFromPreviousCalendarMonth_RemainsCharged()
    {
        var policy = FinancialPolicy();
        policy = policy with { Financial = policy.Financial! with { MonthlyActor = new() { Tokens = 15, Money = 15 } } };
        var store = Store();
        await InitializeAndResumeAsync(store, policy);
        var first = await store.TryAcquireAsync(FinancialContext(), Guid.NewGuid(), Frozen(policy), default);
        first.Granted.Should().BeTrue();
        await store.ReleaseAsync(first.Reservation!, default);
        await ExecuteAsync($"UPDATE \"{_schema}\".ai_financial_invocations SET reserved_at = date_trunc('month', clock_timestamp() AT TIME ZONE 'UTC') AT TIME ZONE 'UTC' - INTERVAL '1 second'");
        (await Store().TryAcquireAsync(FinancialContext(), Guid.NewGuid(), Frozen(policy), default))
            .Code.Should().Be("financial_monthly_actor_exceeded");
    }
}

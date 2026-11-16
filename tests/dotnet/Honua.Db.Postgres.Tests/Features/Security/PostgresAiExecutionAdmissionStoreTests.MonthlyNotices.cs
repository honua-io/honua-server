// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit.Attributes;

namespace Honua.Db.Postgres.Tests.Features.Security;

public sealed partial class PostgresAiExecutionAdmissionStoreTests
{
    [IntegrationTest]
    public async Task MonthlyNotices_RestartAndRetry_PreservePendingIdentityUntilDurablyQueued()
    {
        await SeedMonthlySchemaAsync(30);
        await ExecuteAsync($"UPDATE \"{_schema}\".ai_financial_invocations SET charged_tokens = 24");
        var pending = await Store().ListPendingAsync(10, default);
        var notice = pending.Should().ContainSingle().Subject;
        notice.ChargedTokens.Should().Be(24);
        notice.TokenLimit.Should().Be(30);
        notice.MonthStart.Day.Should().Be(1);
        (await Store().ListPendingAsync(10, default)).Should().Equal(pending,
            "a failed or disabled notification delivery must remain retryable on another replica");
        (await Store().MarkQueuedAsync(notice with { ActorKey = new string('0', 64) }, default)).Should().BeFalse();
        (await Store().MarkQueuedAsync(notice, default)).Should().BeTrue();
        (await Store().MarkQueuedAsync(notice, default)).Should().BeFalse();
        await ApplyMonthlySchemaAsync();
        (await Store().ListPendingAsync(10, default)).Should().BeEmpty();
    }
}

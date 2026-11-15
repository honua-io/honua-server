// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Infrastructure.SharedCaching;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.Caching;

[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class PostgresCdnPurgeBudgetTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    [IntegrationTest]
    public async Task CurrentMonth_AfterCapChanges_ReportsCurrentExhaustion()
    {
        await using var harness = await PostgresJobSubstrateHarness.CreateAsync(database, nameof(PostgresCdnPurgeBudgetTests));
        using var stream = typeof(PostgresFixture).Assembly.GetManifestResourceStream(
            "Honua.Server.Migrations.191_CreateCdnPurgeBudget.sql")!;
        using var reader = new StreamReader(stream);
        await database.ExecuteDdlUnderLockAsync((await reader.ReadToEndAsync())
            .Replace("$HonuaSchema$", $"\"{harness.Schema}\"", StringComparison.Ordinal));
        var options = new SharedCacheOptions();
        var monitor = Substitute.For<IOptionsMonitor<SharedCacheOptions>>();
        monitor.CurrentValue.Returns(options);
        var first = new PostgresCdnPurgeBudget(harness.Database, NullLogger<PostgresCdnPurgeBudget>.Instance, monitor);
        var second = new PostgresCdnPurgeBudget(harness.Database, NullLogger<PostgresCdnPurgeBudget>.Instance, monitor);
        options.PurgeBudget.MonthlyEditSubmissions = 1;
        Assert.Equal(CdnPurgeBudgetDecision.Admitted, (await first.TryAdmitEditAsync(1, 0)).Decision);
        Assert.Equal(CdnPurgeBudgetDecision.Exceeded, (await second.ReadCurrentMonthAsync())!.Value.Decision);

        options.PurgeBudget.MonthlyEditSubmissions = 3;
        Assert.Equal(CdnPurgeBudgetDecision.Admitted, (await second.ReadCurrentMonthAsync())!.Value.Decision);
        Assert.Equal(CdnPurgeBudgetDecision.Admitted, (await second.TryAdmitEditAsync(3, 0)).Decision);
        options.PurgeBudget.MonthlyEditSubmissions = 0;
        Assert.Equal(CdnPurgeBudgetDecision.Admitted, (await second.ReadCurrentMonthAsync())!.Value.Decision);
        options.PurgeBudget.MonthlyEditSubmissions = 2;
        Assert.Equal(CdnPurgeBudgetDecision.Exceeded, (await first.ReadCurrentMonthAsync())!.Value.Decision);
        Assert.Equal(CdnPurgeBudgetDecision.Exceeded, (await first.TryAdmitEditAsync(2, 0)).Decision);
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using FluentAssertions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Db.Postgres.Features.Alerts;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Server.Tests.Features.Alerts;

public sealed partial class AlertLifecycleAuditAtomicityProofTests
{
    [IntegrationTheory]
    [InlineData("2026-03-07T12:00:00Z")]
    [InlineData("2026-10-31T12:00:00Z")]
    public async Task SuppressAsync_DaylightSavingBoundary_EnforcesElapsedHours(string timestamp)
    {
        var settings = new NpgsqlConnectionStringBuilder(_fixture.Postgres.ConnectionString)
        {
            Timezone = "America/Los_Angeles"
        };
        await using var dataSource = NpgsqlDataSource.Create(settings.ConnectionString);
        var provider = new TestDatabaseConnectionProvider(dataSource, () => _schema);
        var store = new PostgresAlertLifecycleStore(provider, _schema);
        var now = DateTimeOffset.Parse(timestamp, CultureInfo.InvariantCulture);

        var accepted = await store.SuppressAsync(_eventId, "operator", now.AddHours(168), null, now);
        accepted!.Status.Should().Be(AlertLifecycleStatus.Suppressed);
        accepted.SuppressedUntil.Should().Be(now.AddHours(168));
        var rejected = await store.SuppressAsync(_eventId, "operator", now.AddHours(168.5), null, now);
        rejected.Should().BeNull();
        (await store.GetAsync(_eventId))!.SuppressedUntil.Should().Be(now.AddHours(168));
    }
}

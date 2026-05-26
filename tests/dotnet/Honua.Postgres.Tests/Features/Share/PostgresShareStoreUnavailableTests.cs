// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using System.Globalization;
using FluentAssertions;
using Honua.Core.Exceptions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Share.Abstractions;
using Honua.Core.Features.Share.Domain;
using Honua.Postgres.Features.Share;
using Npgsql;
using NSubstitute;

namespace Honua.Postgres.Tests.Features.Share;

/// <summary>
/// Verifies Share Postgres stores translate durable store outages to store-unavailable exceptions
/// so admin endpoints can return retryable 503 responses instead of generic 500s.
/// </summary>
public sealed class PostgresShareStoreUnavailableTests
{
    [Fact]
    public async Task TrafficSummary_WhenConnectionFails_ThrowsStoreUnavailable()
    {
        var store = CreateTrafficStore(new NpgsqlException("Failed to connect to 10.0.0.1:5432"));

        var act = () => store.GetSummaryAsync(CreateTrafficQuery());

        await act.Should().ThrowAsync<ShareTrafficStoreUnavailableException>();
    }

    [Fact]
    public async Task TrafficSeries_WhenConnectionTimesOut_ThrowsStoreUnavailable()
    {
        var store = CreateTrafficStore(new TimeoutException("Connection timeout."));

        var act = () => store.GetSeriesAsync(CreateTrafficQuery());

        await act.Should().ThrowAsync<ShareTrafficStoreUnavailableException>();
    }

    [Fact]
    public async Task TrafficSummary_WhenConnectionProviderReportsServiceUnavailable_ThrowsStoreUnavailable()
    {
        var store = CreateTrafficStore(new ServiceUnavailableException("Database connection failed."));

        var act = () => store.GetSummaryAsync(CreateTrafficQuery());

        await act.Should().ThrowAsync<ShareTrafficStoreUnavailableException>();
    }

    [Fact]
    public async Task ExportDefinition_WhenConnectionProviderReportsServiceUnavailable_ThrowsStoreUnavailable()
    {
        var store = new PostgresShareExportStore(
            CreateConnectionProvider(new ServiceUnavailableException("Database connection failed.")));

        var act = () => store.GetDefinitionAsync("definition-1");

        await act.Should().ThrowAsync<ShareExportStoreUnavailableException>();
    }

    private static PostgresShareTrafficStore CreateTrafficStore(Exception exception)
        => new(CreateConnectionProvider(exception));

    private static IDatabaseConnectionProvider CreateConnectionProvider(Exception exception)
    {
        var provider = Substitute.For<IDatabaseConnectionProvider>();
        provider.OpenConnectionAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<DbConnection>(exception));
        return provider;
    }

    private static ShareTrafficQuery CreateTrafficQuery()
        => new()
        {
            ItemRef = null,
            PeriodStart = DateTimeOffset.Parse("2026-05-25T00:00:00Z", CultureInfo.InvariantCulture),
            PeriodEnd = DateTimeOffset.Parse("2026-05-25T02:00:00Z", CultureInfo.InvariantCulture),
            BucketDuration = TimeSpan.FromHours(1)
        };

}

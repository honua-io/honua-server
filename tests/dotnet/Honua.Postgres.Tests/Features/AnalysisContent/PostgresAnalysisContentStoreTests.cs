// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using FluentAssertions;
using Honua.Core.Exceptions;
using Honua.Core.Features.AnalysisContent.Abstractions;
using Honua.Core.Features.AnalysisContent.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Postgres.Features.AnalysisContent;
using Npgsql;
using NSubstitute;

namespace Honua.Postgres.Tests.Features.AnalysisContent;

/// <summary>
/// Verifies that <see cref="PostgresAnalysisContentStore"/> maps provider connectivity failures
/// (Postgres down / unreachable / auth-backend outage) to
/// <see cref="AnalysisContentStoreUnavailableException"/> across read, write, and artifact entry
/// points, so a store outage surfaces as a retryable 503 instead of a generic 500. The connection
/// open itself is the failure point the analysis-content API explicitly models as store-unavailable.
/// </summary>
public sealed class PostgresAnalysisContentStoreTests
{
    [Fact]
    public async Task GetItemAsync_WhenConnectionFails_ThrowsStoreUnavailable()
    {
        var store = CreateStoreWithUnavailableConnection();

        var act = () => store.GetItemAsync("item-1");

        await act.Should().ThrowAsync<AnalysisContentStoreUnavailableException>();
    }

    [Fact]
    public async Task CreateItemAsync_WhenConnectionFails_ThrowsStoreUnavailable()
    {
        var store = CreateStoreWithUnavailableConnection();

        var act = () => store.CreateItemAsync(CreateItem(), CreateVersion());

        await act.Should().ThrowAsync<AnalysisContentStoreUnavailableException>();
    }

    [Fact]
    public async Task GetVersionAsync_WhenConnectionFails_ThrowsStoreUnavailable()
    {
        var store = CreateStoreWithUnavailableConnection();

        var act = () => store.GetVersionAsync("item-1");

        await act.Should().ThrowAsync<AnalysisContentStoreUnavailableException>();
    }

    [Fact]
    public async Task GetArtifactAsync_WhenConnectionFails_ThrowsStoreUnavailable()
    {
        var store = CreateStoreWithUnavailableConnection();

        var act = () => store.GetArtifactAsync("artifact-1");

        await act.Should().ThrowAsync<AnalysisContentStoreUnavailableException>();
    }

    [Fact]
    public async Task CreateItemAsync_WhenConnectionProviderReportsServiceUnavailable_ThrowsStoreUnavailable()
    {
        // The production PostgresDatabaseConnectionProvider classifies connect failures and broken
        // circuits as ServiceUnavailableException (not a raw NpgsqlException), so the store must map
        // that real-outage signal to the documented retryable 503 as well.
        var store = CreateStoreWithServiceUnavailableConnection();

        var act = () => store.CreateItemAsync(CreateItem(), CreateVersion());

        await act.Should().ThrowAsync<AnalysisContentStoreUnavailableException>();
    }

    [Fact]
    public async Task GetItemAsync_WhenConnectionProviderReportsServiceUnavailable_ThrowsStoreUnavailable()
    {
        var store = CreateStoreWithServiceUnavailableConnection();

        var act = () => store.GetItemAsync("item-1");

        await act.Should().ThrowAsync<AnalysisContentStoreUnavailableException>();
    }

    [Fact]
    public async Task UpsertArtifactAsync_WhenConnectionProviderReportsServiceUnavailable_ThrowsStoreUnavailable()
    {
        var store = CreateStoreWithServiceUnavailableConnection();

        var act = () => store.UpsertArtifactAsync(CreateArtifact());

        await act.Should().ThrowAsync<AnalysisContentStoreUnavailableException>();
    }

    private static PostgresAnalysisContentStore CreateStoreWithUnavailableConnection()
    {
        var provider = Substitute.For<IDatabaseConnectionProvider>();
        provider.OpenConnectionAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<DbConnection>(
                new NpgsqlException("Failed to connect to 10.0.0.1:5432")));
        return new PostgresAnalysisContentStore(provider);
    }

    private static PostgresAnalysisContentStore CreateStoreWithServiceUnavailableConnection()
    {
        var provider = Substitute.For<IDatabaseConnectionProvider>();
        provider.OpenConnectionAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<DbConnection>(
                new ServiceUnavailableException("Database connection failed.")));
        return new PostgresAnalysisContentStore(provider);
    }

    private static AnalysisContentItem CreateItem() => new()
    {
        ItemId = "item-1",
        Kind = AnalysisContentKind.SavedQuery,
        Name = "name",
        CurrentVersion = 1,
        CurrentVersionId = "item-1:v1",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static AnalysisContentVersion CreateVersion() => new()
    {
        VersionId = "item-1:v1",
        ItemId = "item-1",
        Version = 1,
        Kind = AnalysisContentKind.SavedQuery,
        SavedQuery = new SavedQueryContent { LayerId = 0, NaturalLanguageQuery = "q" },
        ContentHash = "hash-1",
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static ResultArtifactRecord CreateArtifact() => new()
    {
        ArtifactId = "artifact-1",
        ResultPackageId = "item-1:v1:preview",
        JobId = "preview",
        SourceItemId = "item-1",
        SourceVersion = 1,
        SourceVersionId = "item-1:v1",
        Kind = ArtifactKind.FeatureLayer,
        Label = "preview",
        CreatedAt = DateTimeOffset.UtcNow
    };
}

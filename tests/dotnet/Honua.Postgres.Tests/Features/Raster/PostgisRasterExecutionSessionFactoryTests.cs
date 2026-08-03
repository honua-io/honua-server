// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Postgres.Features.Raster;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Honua.Postgres.Tests.Features.Raster;

public sealed class PostgisRasterExecutionSessionFactoryTests
{
    private const string ConnectionString =
        "Host=localhost;Database=honua;Username=raster_role;Password=test";

    [Fact]
    public async Task ExecuteAsync_PassesPinnedAttemptAndExactCancellationToken()
    {
        var options = Options();
        await using var dataSource = PostgisRasterDataSource.Create(ConnectionString, options);
        using var admission = new PostgisRasterAdmissionController(
            Microsoft.Extensions.Options.Options.Create(options));
        var factory = new PostgisRasterExecutionSessionFactory(
            dataSource,
            admission,
            Microsoft.Extensions.Options.Options.Create(options));
        var request = PostgisRasterGovernanceTestData.Request();
        using var cancellation = new CancellationTokenSource();
        PostgisRasterExecutionSession? captured = null;
        CancellationToken capturedToken = default;

        var result = await factory.ExecuteAsync(
            request,
            (session, cancellationToken) =>
            {
                captured = session;
                capturedToken = cancellationToken;
                return Task.FromResult(RasterProviderExecutionResult.Succeeded([]));
            },
            cancellation.Token);

        result.Status.Should().Be(RasterProviderExecutionStatus.Succeeded);
        captured.Should().NotBeNull();
        captured!.TenantId.Should().Be(request.TenantId);
        captured.OperationId.Should().Be(request.OperationId);
        captured.Attempt.Should().Be(request.Attempt);
        captured.ConnectionProvider.Should().BeOfType<PostgisRasterDatabaseConnectionProvider>();
        capturedToken.Should().Be(cancellation.Token);
    }

    [Fact]
    public async Task ExecuteAsync_ActiveCancellationPropagatesAndReleasesAdmission()
    {
        var options = Options();
        options.MaxConcurrency = 1;
        options.MaxConcurrencyPerTenant = 1;
        await using var dataSource = PostgisRasterDataSource.Create(ConnectionString, options);
        using var admission = new PostgisRasterAdmissionController(
            Microsoft.Extensions.Options.Options.Create(options));
        var factory = new PostgisRasterExecutionSessionFactory(
            dataSource,
            admission,
            Microsoft.Extensions.Options.Options.Create(options));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var execution = factory.ExecuteAsync(
            PostgisRasterGovernanceTestData.Request(),
            async (_, cancellationToken) =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return RasterProviderExecutionResult.Succeeded([]);
            },
            cancellation.Token);
        await entered.Task;

        await cancellation.CancelAsync();

        var act = async () => await execution;
        await act.Should().ThrowAsync<OperationCanceledException>();
        await using var nextLease = await admission.AcquireAsync(
            PostgisRasterGovernanceTestData.Request(),
            CancellationToken.None);
        nextLease.Should().NotBeNull();
    }

    [Theory]
    [InlineData(PostgresErrorCodes.QueryCanceled, "postgis-raster-statement-timeout")]
    [InlineData(PostgresErrorCodes.LockNotAvailable, "postgis-raster-lock-timeout")]
    [InlineData(PostgresErrorCodes.DeadlockDetected, "postgis-raster-deadlock")]
    [InlineData(PostgresErrorCodes.SerializationFailure, "postgis-raster-serialization-failure")]
    [InlineData(PostgresErrorCodes.TooManyConnections, "postgis-raster-database-unavailable")]
    public void FailureClassifier_KnownTransientSqlState_ReturnsStableRetryableCode(
        string sqlState,
        string errorCode)
    {
        var exception = new PostgresException("test", "ERROR", "ERROR", sqlState);

        var classified = PostgisRasterFailureClassifier.TryClassify(exception, out var failure);

        classified.Should().BeTrue();
        failure.ErrorCode.Should().Be(errorCode);
        failure.IsRetryable.Should().BeTrue();
    }

    [Fact]
    public void FailureClassifier_ApplicationFailure_DoesNotInventRetryability()
    {
        var classified = PostgisRasterFailureClassifier.TryClassify(
            new InvalidOperationException("operation input failed"),
            out _);

        classified.Should().BeFalse();
    }

    [Fact]
    public void FailureClassifier_PermanentDatabaseFailure_ReturnsBoundedStableCode()
    {
        var exception = new PostgresException(
            "sensitive database detail",
            "ERROR",
            "ERROR",
            PostgresErrorCodes.UniqueViolation);

        var classified = PostgisRasterFailureClassifier.TryClassify(exception, out var failure);

        classified.Should().BeTrue();
        failure.ErrorCode.Should().Be("postgis-raster-database-error");
        failure.Message.Should().NotContain("sensitive");
        failure.IsRetryable.Should().BeFalse();
    }

    private static PostgisRasterExecutionOptions Options() => new()
    {
        RequiredRole = "raster_role",
        SearchPathSchema = "honua",
        MaxConcurrency = 2,
        MaxConcurrencyPerTenant = 1,
        QueueTimeout = TimeSpan.FromSeconds(1),
    };
}

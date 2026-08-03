// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Honua.Postgres.Features.Raster;

internal interface IPostgisRasterExecutionSessionFactory
{
    Task<RasterProviderExecutionResult> ExecuteAsync(
        RasterProviderExecutionRequest request,
        Func<PostgisRasterExecutionSession, CancellationToken, Task<RasterProviderExecutionResult>> operation,
        CancellationToken cancellationToken);
}

internal sealed record PostgisRasterExecutionSession(
    string TenantId,
    string OperationId,
    int Attempt,
    IAdoNetDatabaseConnectionProvider ConnectionProvider);

internal sealed class PostgisRasterExecutionSessionFactory : IPostgisRasterExecutionSessionFactory
{
    private readonly PostgisRasterDataSource _dataSource;
    private readonly PostgisRasterAdmissionController _admission;
    private readonly PostgisRasterExecutionOptions _options;
    private readonly ITenantSchemaResolver? _tenantSchemaResolver;

    public PostgisRasterExecutionSessionFactory(
        PostgisRasterDataSource dataSource,
        PostgisRasterAdmissionController admission,
        IOptions<PostgisRasterExecutionOptions> options,
        ITenantSchemaResolver? tenantSchemaResolver = null)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _tenantSchemaResolver = tenantSchemaResolver;
    }

    public async Task<RasterProviderExecutionResult> ExecuteAsync(
        RasterProviderExecutionRequest request,
        Func<PostgisRasterExecutionSession, CancellationToken, Task<RasterProviderExecutionResult>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(operation);

        try
        {
            await using var admission = await _admission.AcquireAsync(request, cancellationToken)
                .ConfigureAwait(false);
            var session = new PostgisRasterExecutionSession(
                request.TenantId,
                request.OperationId,
                request.Attempt,
                new PostgisRasterDatabaseConnectionProvider(
                    _dataSource,
                    _options,
                    request.TenantId,
                    request.OperationId,
                    request.Attempt,
                    ResolveSchema(request.TenantId)));

            return await operation(session, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (PostgisRasterGovernanceException ex)
        {
            return ex.ToResult();
        }
        catch (Exception ex) when (PostgisRasterFailureClassifier.TryClassify(ex, out var failure))
        {
            return RasterProviderExecutionResult.Failed(
                failure.ErrorCode,
                failure.Message,
                failure.IsRetryable);
        }
    }

    private string ResolveSchema(string tenantId)
    {
        if (!_options.RequireTenantSchema)
        {
            return _options.SearchPathSchema;
        }

        if (_tenantSchemaResolver is null ||
            !_tenantSchemaResolver.TryResolveSchema(tenantId, out var schemaName))
        {
            throw PostgisRasterGovernanceException.TenantSchemaUnavailable();
        }

        return schemaName;
    }
}

internal readonly record struct PostgisRasterFailure(
    string ErrorCode,
    string Message,
    bool IsRetryable);

internal static class PostgisRasterFailureClassifier
{
    public static bool TryClassify(Exception exception, out PostgisRasterFailure failure)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is PostgresException postgresException)
        {
            failure = postgresException.SqlState switch
            {
                PostgresErrorCodes.QueryCanceled => Retryable(
                    "postgis-raster-statement-timeout",
                    "The PostGIS raster statement exceeded its server-side timeout."),
                PostgresErrorCodes.LockNotAvailable => Retryable(
                    "postgis-raster-lock-timeout",
                    "The PostGIS raster statement could not acquire a database lock in time."),
                PostgresErrorCodes.DeadlockDetected => Retryable(
                    "postgis-raster-deadlock",
                    "The PostGIS raster transaction was selected as a deadlock victim."),
                PostgresErrorCodes.SerializationFailure => Retryable(
                    "postgis-raster-serialization-failure",
                    "The PostGIS raster transaction encountered a serialization conflict."),
                PostgresErrorCodes.AdminShutdown or
                PostgresErrorCodes.CrashShutdown or
                PostgresErrorCodes.CannotConnectNow => Retryable(
                    "postgis-raster-database-unavailable",
                    "The dedicated PostGIS raster database is temporarily unavailable."),
                _ when postgresException.SqlState.StartsWith("08", StringComparison.Ordinal) ||
                    postgresException.SqlState.StartsWith("53", StringComparison.Ordinal) => Retryable(
                        "postgis-raster-database-unavailable",
                        "The dedicated PostGIS raster database is temporarily unavailable."),
                _ => Permanent(
                    "postgis-raster-database-error",
                    "The dedicated PostGIS raster database rejected the raster operation."),
            };

            return true;
        }

        if (exception is NpgsqlException)
        {
            failure = Retryable(
                "postgis-raster-database-unavailable",
                "The dedicated PostGIS raster database is temporarily unavailable.");
            return true;
        }

        if (exception is TimeoutException)
        {
            failure = Retryable(
                "postgis-raster-database-timeout",
                "The dedicated PostGIS raster database operation timed out.");
            return true;
        }

        failure = default;
        return false;
    }

    private static PostgisRasterFailure Retryable(string errorCode, string message) =>
        new(errorCode, message, IsRetryable: true);

    private static PostgisRasterFailure Permanent(string errorCode, string message) =>
        new(errorCode, message, IsRetryable: false);
}

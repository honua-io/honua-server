// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Infrastructure.SharedCaching;
using Honua.ServiceDefaults;
using Npgsql;

namespace Honua.ControlPlane;

// A new task definition replaces tasks without any server-side promote, so the release change is detected where it is certain:
// a task that starts on a release other than the one last recorded tells the CDN once. The record is one row changed by
// compare-and-set, so the tasks of one deployment produce one invalidation between them.
internal sealed partial class ReleaseChangeCdnInvalidation(
    IServiceProvider services,
    CdnInvalidationService invalidation,
    ILogger<ReleaseChangeCdnInvalidation> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (HonuaDeploymentIdentity.Revision is not { Length: > 0 } release
            || services.GetService<ICdnInvalidator>() is null or NoCdnInvalidator
            || services.GetService<NpgsqlDataSource>() is not { } dataSource)
        {
            return;
        }

        try
        {
            if (await new PostgresCdnReleaseRecord(dataSource).TryRecordChangeAsync(release, stoppingToken).ConfigureAwait(false))
            {
                var result = await invalidation.InvalidateNowAsync(CdnPaths.Everything, stoppingToken).ConfigureAwait(false);
                var outcome = result.Outcome.ToString();
                Log.ReleaseChanged(logger, release, outcome, result.Detail ?? string.Empty);
            }
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException)
        {
            Log.ReleaseRecordUnavailable(logger, exception);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 10320,
            Level = LogLevel.Information,
            Message = "This task started a release the CDN had not been told about. release={Release} invalidation={Outcome} detail={Detail}")]
        public static partial void ReleaseChanged(ILogger logger, string release, string outcome, string detail);

        [LoggerMessage(
            EventId = 10321,
            Level = LogLevel.Warning,
            Message = "The released version could not be compared with the last one recorded; the CDN was not invalidated at startup.")]
        public static partial void ReleaseRecordUnavailable(ILogger logger, Exception exception);
    }
}

internal sealed class PostgresCdnReleaseRecord(NpgsqlDataSource dataSource)
{
    internal const string TryRecordChangeSql = """
        INSERT INTO honua.cdn_release_record (singleton, release_id)
        VALUES (TRUE, @release)
        ON CONFLICT (singleton) DO UPDATE
            SET release_id = EXCLUDED.release_id, recorded_at = now()
            WHERE honua.cdn_release_record.release_id IS DISTINCT FROM EXCLUDED.release_id
        RETURNING release_id
        """;

    internal async Task<bool> TryRecordChangeAsync(string release, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(TryRecordChangeSql, connection);
        command.Parameters.AddWithValue("release", release);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }
}

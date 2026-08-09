// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using System.Collections.Immutable;
using Honua.DuckDB;
using Microsoft.Extensions.Logging;

namespace Honua.DuckDB.Features.Infrastructure;

/// <summary>
/// Installs the DuckDB spatial extension once per process and loads it onto every
/// connection handed out by <see cref="DuckDBConnectionProvider"/>.
/// Supports an optional offline extension directory for air-gapped deployments.
/// </summary>
internal sealed class DuckDBSpatialBootstrap : IDisposable
{
    private static readonly ImmutableArray<string> SpatialExtension = ["spatial"];
    private readonly string? _extensionPath;
    private readonly ImmutableArray<string> _extensions;
    private readonly ImmutableArray<DuckDBBootstrapSqlCommand> _connectionSettingCommands;
    private readonly ImmutableArray<DuckDBExternalSourcePlan> _externalSourcePlans;
    private readonly ILogger<DuckDBSpatialBootstrap> _logger;
    private readonly SemaphoreSlim _installLock = new(1, 1);
    private readonly HashSet<string> _installedExtensions = new(StringComparer.OrdinalIgnoreCase);

    public DuckDBSpatialBootstrap(string? extensionPath, ILogger<DuckDBSpatialBootstrap> logger)
        : this(
            extensionPath,
            [],
            [],
            [],
            logger)
    {
    }

    public DuckDBSpatialBootstrap(
        string? extensionPath,
        IEnumerable<string> additionalExtensions,
        IEnumerable<DuckDBBootstrapSqlCommand> connectionSettingCommands,
        IEnumerable<DuckDBExternalSourcePlan> externalSourcePlans,
        ILogger<DuckDBSpatialBootstrap> logger)
    {
        _extensionPath = extensionPath;
        _extensions = SpatialExtension
            .AddRange(additionalExtensions.Where(extension => !string.IsNullOrWhiteSpace(extension)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        _connectionSettingCommands = connectionSettingCommands.ToImmutableArray();
        _externalSourcePlans = externalSourcePlans.ToImmutableArray();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Ensures the spatial extension is installed (once per process) and loaded on the
    /// given connection. <c>LOAD spatial</c> and <c>SET extension_directory</c> are
    /// connection-scoped in DuckDB, so they must run on every freshly opened connection
    /// or query execution will fail when spatial <c>ST_*</c> functions are referenced.
    /// </summary>
    public async Task EnsureSpatialExtensionAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        // SET extension_directory is connection-scoped — apply on every fresh connection
        // when an offline extension path is configured so LOAD can locate the artifact.
        if (!string.IsNullOrWhiteSpace(_extensionPath))
        {
            await ExecuteNonQueryAsync(
                    connection,
                    new DuckDBBootstrapSqlCommand(
                        $"SET extension_directory={DuckDBExternalSourceSql.QuoteLiteral(_extensionPath)}",
                        "configure DuckDB extension directory"),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var extension in _extensions)
        {
            await EnsureExtensionInstalledAsync(connection, extension, cancellationToken).ConfigureAwait(false);

            // LOAD is connection-scoped — must run on every new connection so extension
            // functions and filesystems are available for query execution.
            await ExecuteNonQueryAsync(
                    connection,
                    new DuckDBBootstrapSqlCommand(
                        $"LOAD {extension}",
                        $"load DuckDB {extension} extension"),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var command in _connectionSettingCommands)
        {
            await ExecuteNonQueryAsync(connection, command, cancellationToken).ConfigureAwait(false);
        }

        foreach (var plan in _externalSourcePlans)
        {
            await ExecuteNonQueryAsync(
                    connection,
                    new DuckDBBootstrapSqlCommand(
                        plan.CreateViewSql,
                        $"create DuckDB external source view '{plan.ViewName}' for layer {plan.LayerId}"),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _installLock.Dispose();
    }

    private async Task EnsureExtensionInstalledAsync(
        DbConnection connection,
        string extension,
        CancellationToken cancellationToken)
    {
        await _installLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_installedExtensions.Contains(extension))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(_extensionPath))
            {
                DuckDbLog.InstallingDuckDbExtensionFromOfflinePath(_logger, extension, _extensionPath);
            }

            await ExecuteNonQueryAsync(
                    connection,
                    new DuckDBBootstrapSqlCommand(
                        $"INSTALL {extension}",
                        $"install DuckDB {extension} extension"),
                    cancellationToken)
                .ConfigureAwait(false);

            _installedExtensions.Add(extension);
            DuckDbLog.DuckDbExtensionInstalled(_logger, extension);
        }
        finally
        {
            _installLock.Release();
        }
    }

    private static async Task ExecuteNonQueryAsync(
        DbConnection connection,
        DuckDBBootstrapSqlCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = command.Sql;
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"DuckDB connection bootstrap failed while attempting to {command.Description}. " +
                "Check DuckDB extension availability, object-store credentials, and external source configuration.",
                ex);
        }
    }
}

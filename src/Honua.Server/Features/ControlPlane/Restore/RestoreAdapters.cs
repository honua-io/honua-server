// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data.Common;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Restore;
using Honua.Core.Features.Infrastructure.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Honua.ControlPlane;

/// <summary>Opens the database the schema probe should read.</summary>
internal interface IRestoredConnectionOpener
{
    /// <summary>Opens the app database when <paramref name="endpoint"/> is null, otherwise that host.</summary>
    Task<DbConnection?> OpenAsync(string? endpoint, CancellationToken cancellationToken);
}

/// <summary>Swaps only the host of a Postgres connection string. The result must not be logged.</summary>
internal static class ConnectionStringHostSwap
{
    /// <summary>Replaces the host. False when the string cannot be parsed or the host is not a single hostname.</summary>
    public static bool TrySwapHost(string connectionString, string host, out string swapped)
    {
        swapped = string.Empty;
        if (string.IsNullOrWhiteSpace(connectionString) || !IsHost(host))
        {
            return false;
        }

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString) { Host = host };
            swapped = builder.ConnectionString;
            return string.Equals(builder.Host, host, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            swapped = string.Empty;
            return false;
        }
    }

    /// <summary>Reads the host, or null when the string cannot be parsed.</summary>
    public static string? ReadHost(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        try
        {
            return new NpgsqlConnectionStringBuilder(connectionString).Host;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            return null;
        }
    }

    private static bool IsHost(string host)
        => !string.IsNullOrWhiteSpace(host)
           && !host.Contains(';', StringComparison.Ordinal)
           && !host.Contains('=', StringComparison.Ordinal)
           && !host.Contains(' ', StringComparison.Ordinal)
           && !host.Contains('/', StringComparison.Ordinal);
}

/// <summary>Reads applied migrations. Any failure is an unknown journal.</summary>
internal static class SchemaJournalReader
{
    /// <summary>Reads <c>public.schema_versions</c> and the reader-compatibility table when it exists.</summary>
    public static async Task<SchemaJournalSnapshot> ReadAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            if (!await TableExistsAsync(connection, "public.schema_versions", cancellationToken).ConfigureAwait(false))
            {
                return SchemaJournalSnapshot.Unknown;
            }

            var compatibility = await TableExistsAsync(connection, "public.schema_version_reader_compatibility", cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = compatibility
                ? """
                  SELECT v.scriptname, c.classification, c.min_reader_schema, c.declaration_format, c.script_sha256, c.script_text
                  FROM public.schema_versions v
                  LEFT JOIN public.schema_version_reader_compatibility c ON c.script_name = v.scriptname
                  """
                : "SELECT scriptname, NULL::text, NULL::integer, NULL::integer, NULL::text, NULL::text FROM public.schema_versions";
            var applied = new List<AppliedSchemaMigration>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var name = reader.IsDBNull(0) ? null : reader.GetString(0);
                var number = name is null ? null : MigrationReaderCompatibility.ReadSchemaNumber(name);
                if (number is null)
                {
                    return SchemaJournalSnapshot.Unknown;
                }

                var classification = reader.IsDBNull(1) ? null : reader.GetString(1);
                var minReader = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
                if (classification == MigrationReaderCompatibilityClassification.Expand)
                {
                    if (reader.IsDBNull(3) || reader.IsDBNull(4) || reader.IsDBNull(5))
                        classification = null;
                    else
                    {
                        classification = VerifyClassification(name!, reader.GetInt32(3), classification,
                            minReader, reader.GetString(4), reader.GetString(5));
                    }
                }
                applied.Add(new AppliedSchemaMigration(number.Value, classification, minReader));
            }

            return new SchemaJournalSnapshot(true, applied);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return SchemaJournalSnapshot.Unknown;
        }
    }

    internal static string? VerifyClassification(string name, int format, string classification, int minReader, string hash, string text)
    {
        var declaration = new MigrationReaderCompatibilityDeclaration
        {
            ScriptName = name, DeclarationFormat = format, Classification = classification,
            MinReaderSchema = minReader, ScriptSha256 = hash, ScriptText = text
        };
        return MigrationReaderCompatibility.Verify(declaration, name, int.MaxValue).Accepted ? classification : null;
    }

    private static async Task<bool> TableExistsAsync(DbConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT to_regclass(@table) IS NOT NULL";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "table";
        parameter.Value = table;
        command.Parameters.Add(parameter);
        var scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return scalar is true;
    }
}

/// <summary>Journal of the process database. Missing data source or a failed read is unknown.</summary>
internal sealed class PostgresSchemaJournal(IServiceProvider services) : ISchemaJournal
{
    /// <inheritdoc />
    public async Task<SchemaJournalSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        var dataSource = services.GetService<NpgsqlDataSource>();
        if (dataSource is null)
        {
            return SchemaJournalSnapshot.Unknown;
        }

        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            return await SchemaJournalReader.ReadAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return SchemaJournalSnapshot.Unknown;
        }
    }
}

/// <summary>Prior-image schema numbers from <c>ControlPlane:Restore:Revisions</c>.</summary>
internal sealed class ConfiguredRevisionSchemaCatalog(IOptions<RestoreCellOptions> options) : IRevisionSchemaCatalog
{
    /// <inheritdoc />
    public int? MaxSchema(string revision)
    {
        foreach (var entry in options.Value.Revisions)
        {
            if (string.Equals(entry.Revision, revision, StringComparison.Ordinal))
            {
                return entry.MaxSchema;
            }
        }

        return null;
    }
}

/// <summary>Append-only audit file. An empty path or a failed write is not durable.</summary>
internal sealed class FilePreMigrationRestoreAudit(IOptions<RestoreCellOptions> options) : IPreMigrationRestoreAudit
{
    /// <inheritdoc />
    public async Task<bool> AppendAsync(PreMigrationRestoreAuditLine line, CancellationToken cancellationToken)
    {
        var path = options.Value.AuditPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(line, PreMigrationRestoreJsonContext.Default.PreMigrationRestoreAuditLine) + "\n";
            var bytes = Encoding.UTF8.GetBytes(json);
            await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<OperationBackendStatus?> FindAsync(string operationId, CancellationToken cancellationToken)
    {
        var path = options.Value.AuditPath;
        if (string.IsNullOrWhiteSpace(operationId) || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            OperationBackendStatus? found = null;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.Asynchronous);
            using var reader = new StreamReader(stream);
            while (true)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    return found;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                PreMigrationRestoreAuditLine? parsed;
                try
                {
                    parsed = JsonSerializer.Deserialize(line, PreMigrationRestoreJsonContext.Default.PreMigrationRestoreAuditLine);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (parsed is null || !string.Equals(parsed.OperationId, operationId, StringComparison.Ordinal))
                {
                    continue;
                }

                found = parsed.Ok && parsed.ReadbackComplete && string.IsNullOrEmpty(parsed.Code)
                    ? new OperationBackendStatus { State = OperationBackendState.Succeeded }
                    : new OperationBackendStatus { State = OperationBackendState.Failed, Reason = parsed.Code };
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }
}

/// <summary>Probes schema through <see cref="IRestoredConnectionOpener"/>. A closed opener is an unknown journal.</summary>
internal sealed class JournalRestoredSchemaProbe(IRestoredConnectionOpener opener) : IRestoredSchemaProbe
{
    /// <inheritdoc />
    public async Task<SchemaJournalSnapshot> ProbeAsync(string? endpoint, CancellationToken cancellationToken)
    {
        DbConnection? connection;
        try
        {
            connection = await opener.OpenAsync(endpoint, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return SchemaJournalSnapshot.Unknown;
        }

        if (connection is null)
        {
            return SchemaJournalSnapshot.Unknown;
        }

        await using (connection)
        {
            return await SchemaJournalReader.ReadAsync(connection, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>One GET of the configured customer URL. Empty URL, non-200, or an empty body is incomplete.</summary>
internal sealed class HttpServingCustomerProbe(IHttpClientFactory httpClientFactory, IOptions<RestoreCellOptions> options) : IServingCustomerProbe
{
    /// <inheritdoc />
    public async Task<bool> SucceededAsync(CancellationToken cancellationToken)
    {
        var url = options.Value.CustomerProbeUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        try
        {
            var client = httpClientFactory.CreateClient(ControlPlaneHttpClients.Probe);
            client.Timeout = TimeSpan.FromSeconds(30);
            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return false;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return !string.IsNullOrWhiteSpace(body);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }
}

/// <summary>
/// Sends one <c>rollback</c> line to the Compose sidecar socket. This process does not talk to Docker.
/// The sidecar both restores and starts the image; a missing socket is a failure.
/// </summary>
internal sealed class ComposeRollbackSocketClient(IOptions<RestoreCellOptions> options) : IComposeRollbackTransport
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(10);

    /// <inheritdoc />
    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.Value.ComposeSocketPath);

    /// <inheritdoc />
    public async Task<ComposeRollbackResult> RollbackAsync(
        string image,
        string? backupId,
        string? requestId,
        CancellationToken cancellationToken)
    {
        var socketPath = options.Value.ComposeSocketPath;
        if (string.IsNullOrWhiteSpace(socketPath))
        {
            return Unavailable();
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), timeout.Token).ConfigureAwait(false);
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            var request = new ComposeRollbackWireRequest
            {
                Op = "rollback",
                Image = image,
                BackupId = backupId,
                RequestId = requestId,
            };
            var line = JsonSerializer.Serialize(request, PreMigrationRestoreJsonContext.Default.ComposeRollbackWireRequest);
            var payload = Encoding.UTF8.GetBytes(line + "\n");
            await stream.WriteAsync(payload, timeout.Token).ConfigureAwait(false);
            var responseLine = await ReadLineAsync(stream, timeout.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(responseLine))
            {
                return Failed();
            }

            var parsed = JsonSerializer.Deserialize(responseLine, ComposeRollbackResponseJsonContext.Default.ComposeRollbackWireResponse);
            if (parsed is null)
            {
                return Failed();
            }

            var started = parsed.Ok && parsed.ReadbackComplete;
            return new ComposeRollbackResult(parsed.Ok, parsed.Code, parsed.ReadbackComplete, parsed.SchemaVersion, started);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Unavailable();
        }
    }

    private static async Task<string> ReadLineAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var one = new byte[1];
        while (buffer.Length <= 64 * 1024)
        {
            var read = await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false);
            if (read == 0 || one[0] == (byte)'\n')
            {
                break;
            }

            buffer.WriteByte(one[0]);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static ComposeRollbackResult Unavailable()
        => new(false, PreMigrationRestoreCodes.TransportUnavailable, false, null, false);

    private static ComposeRollbackResult Failed()
        => new(false, PreMigrationRestoreCodes.ProviderFailed, false, null, false);
}

/// <summary>Cloud restore is absent from this build. Describe and restore fail closed.</summary>
internal sealed class UnconfiguredRdsSnapshotRestore : IRdsSnapshotRestore
{
    /// <inheritdoc />
    public Task<RdsSnapshotEvidence?> DescribeAsync(string snapshotId, string cellPrefix, CancellationToken cancellationToken)
        => Task.FromResult<RdsSnapshotEvidence?>(null);

    /// <inheritdoc />
    public Task<RdsNewInstance> RestoreNewAsync(RdsRestoreRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new RdsNewInstance(false, null, PreMigrationRestoreCodes.RetargetUnsupported));
}

/// <summary>No connection secret can be repointed in this build.</summary>
internal sealed class UnconfiguredConnectionSecretRetarget : IConnectionSecretRetarget
{
    /// <inheritdoc />
    public bool IsConfigured => false;

    /// <inheritdoc />
    public Task<bool> PointAtAsync(string secretId, string host, CancellationToken cancellationToken)
        => Task.FromResult(false);

    /// <inheritdoc />
    public Task<string?> ReadHostAsync(string secretId, CancellationToken cancellationToken)
        => Task.FromResult<string?>(null);
}

/// <summary>Opens only the process database. A restored endpoint cannot be probed without the cloud secret.</summary>
internal sealed class AppDatabaseConnectionOpener(IServiceProvider services) : IRestoredConnectionOpener
{
    /// <inheritdoc />
    public async Task<DbConnection?> OpenAsync(string? endpoint, CancellationToken cancellationToken)
    {
        if (endpoint is not null)
        {
            return null;
        }

        var dataSource = services.GetService<NpgsqlDataSource>();
        if (dataSource is null)
        {
            return null;
        }

        return await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
    }
}

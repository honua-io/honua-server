// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.Observability.Domain;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.Observability;

internal sealed class PostgresOpsIncidentEpisodeStore(
    IAdoNetDatabaseConnectionProvider connections, string? schemaName)
    : IOpsIncidentEpisodeStore, IOpsIncidentEpisodeResolver
{
    private readonly string _episodes = SchemaSearchPath.QualifyTable("ops_incident_episodes", schemaName);
    private readonly string _observe = SchemaSearchPath.QualifyTable("observe_ops_incident_episode", schemaName);

    public async Task<OpsIncidentEpisode?> ObserveAsync(
        OpsIncidentEpisodeSignal signal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ValidateIdentifier(signal.AuthorityScope);
        ValidateIdentifier(signal.FindingId);
        ValidateIdentifier(signal.ObservationId);
        if (!Enum.IsDefined(signal.State) || signal.ObservedAt > DateTimeOffset.UtcNow.AddSeconds(30))
        {
            throw new ArgumentException("Incident observations require a valid state and a non-future timestamp.", nameof(signal));
        }
        await using var connection = await connections.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"SELECT * FROM {_observe}(@authority, @finding, @observation, @observed, @state)", connection);
        command.Parameters.AddWithValue("authority", signal.AuthorityScope);
        command.Parameters.AddWithValue("finding", signal.FindingId);
        command.Parameters.AddWithValue("observation", signal.ObservationId);
        command.Parameters.AddWithValue("observed", NpgsqlDbType.TimestampTz, signal.ObservedAt.ToUniversalTime());
        command.Parameters.AddWithValue("state", NpgsqlDbType.Smallint, (short)signal.State);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadEpisode(reader) : null;
    }

    public Task<OpsIncidentEpisode?> ReadAsync(
        OpsIncidentEpisodeIdentity identity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return ReadAsync(new EpisodeQuery(identity.AuthorityScope, identity.EpisodeId, null), cancellationToken);
    }

    public Task<OpsIncidentEpisode?> ResolveAsync(
        OpsIncidentEpisodeReference reference, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return ReadAsync(new EpisodeQuery(reference.AuthorityScope, reference.EpisodeId, reference.LifecycleVersion), cancellationToken);
    }

    private async Task<OpsIncidentEpisode?> ReadAsync(EpisodeQuery query, CancellationToken cancellationToken)
    {
        ValidateIdentifier(query.AuthorityScope);
        var admission = query.LifecycleVersion.HasValue
            ? "AND lifecycle_version = @version AND state = 0 AND closed_at IS NULL " +
              "AND observed_at >= now() - interval '5 minutes' AND observed_at <= now() + interval '30 seconds'"
            : string.Empty;
        await using var connection = await connections.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"SELECT * FROM {_episodes} WHERE authority_scope = @authority AND episode_id = @episode {admission}", connection);
        command.Parameters.AddWithValue("authority", query.AuthorityScope);
        command.Parameters.AddWithValue("episode", query.EpisodeId);
        if (query.LifecycleVersion.HasValue)
        {
            command.Parameters.AddWithValue("version", query.LifecycleVersion.Value);
        }
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadEpisode(reader) : null;
    }

    private static OpsIncidentEpisode ReadEpisode(NpgsqlDataReader reader) => new()
    {
        AuthorityScope = reader.GetString(0),
        EpisodeId = reader.GetGuid(1),
        FindingId = reader.GetString(2),
        LifecycleVersion = reader.GetInt64(3),
        State = (OpsIncidentEpisodeState)reader.GetInt16(4),
        StartedAt = reader.GetFieldValue<DateTimeOffset>(5),
        ObservedAt = reader.GetFieldValue<DateTimeOffset>(6),
        ClosedAt = reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7)
    };

    private static void ValidateIdentifier(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (Encoding.UTF8.GetByteCount(value) > 256)
        {
            throw new ArgumentException("Incident identifiers must fit within 256 UTF-8 bytes.", nameof(value));
        }
    }

    private sealed record EpisodeQuery(string AuthorityScope, Guid EpisodeId, long? LifecycleVersion);
}

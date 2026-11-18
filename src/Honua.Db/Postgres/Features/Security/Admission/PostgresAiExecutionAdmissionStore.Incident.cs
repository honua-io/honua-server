// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Security.Admission;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Db.Postgres.Features.Security.Admission;

internal sealed partial class PostgresAiExecutionAdmissionStore
{
    private readonly string _incidentEpisodes = SchemaSearchPath.QualifyTable("ops_incident_episodes", schemaName);

    private async Task<string?> CheckIncidentReservationAsync(MonthlyActorReservation reservation,
        AiIncidentWorkflowBinding? binding, CancellationToken cancellationToken)
    {
        if (reservation.Policy.Incident is not { } limit) return null;
        if (binding is null) return "financial_incident_required";
        await using var episode = CreateIncidentLockCommand(reservation.Connection, reservation.Transaction);
        BindIncident(episode, binding);
        if (await episode.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            return "financial_incident_invalid";
        await using var counts = new NpgsqlCommand($"""
            SELECT COALESCE(SUM(charged_tokens), 0), COALESCE(SUM(charged_money), 0)
            FROM {_financial} WHERE deployment_id = @deployment
                AND incident_authority = @incident_authority AND incident_episode = @incident_episode
            """, reservation.Connection, reservation.Transaction);
        counts.Parameters.AddWithValue("deployment", reservation.Context.DeploymentId);
        BindIncident(counts, binding);
        await using var reader = await counts.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return reader.GetDecimal(0) + reservation.Tokens > limit.Tokens
            || reader.GetDecimal(1) + reservation.Money > limit.Money ? "financial_incident_exceeded" : null;
    }

    private NpgsqlCommand CreateIncidentLockCommand(NpgsqlConnection connection, NpgsqlTransaction transaction)
        => new($"""
            SELECT true FROM {_incidentEpisodes}
            WHERE authority_scope = @incident_authority AND episode_id = @incident_episode
                AND lifecycle_version = @incident_version AND state = 0 AND closed_at IS NULL
                AND observed_at >= clock_timestamp() - interval '5 minutes'
                AND observed_at <= clock_timestamp() + interval '30 seconds'
            FOR SHARE
            """, connection, transaction);

    private sealed record IncidentSend(NpgsqlConnection Connection, NpgsqlTransaction Transaction, AiAdmissionReservation Reservation);

    private async Task<bool> ValidateIncidentSendAsync(IncidentSend send, CancellationToken cancellationToken)
    {
        AiIncidentWorkflowBinding binding;
        await using (var query = new NpgsqlCommand($"""
            SELECT incident_authority, incident_episode, incident_version FROM {_financial}
            WHERE deployment_id = @deployment AND attempt_id = @attempt
            """, send.Connection, send.Transaction))
        {
            query.Parameters.AddWithValue("deployment", send.Reservation.DeploymentId);
            query.Parameters.AddWithValue("attempt", send.Reservation.AttemptId);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.IsDBNull(0)) return false;
            binding = new() { AuthorityScope = reader.GetString(0), EpisodeId = reader.GetGuid(1), LifecycleVersion = reader.GetInt64(2) };
        }
        await using var episode = CreateIncidentLockCommand(send.Connection, send.Transaction);
        BindIncident(episode, binding);
        return await episode.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    private static void BindIncident(NpgsqlCommand command, AiIncidentWorkflowBinding binding)
    {
        command.Parameters.AddWithValue("incident_authority", binding.AuthorityScope);
        command.Parameters.AddWithValue("incident_episode", binding.EpisodeId);
        command.Parameters.AddWithValue("incident_version", binding.LifecycleVersion);
    }
}

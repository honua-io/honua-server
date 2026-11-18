// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Security.Admission;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.Security.Admission;

internal sealed partial class PostgresAiExecutionAdmissionStore : IAiIncidentNoticeStore
{
    private readonly string _incidentNotices = SchemaSearchPath.QualifyTable("ai_incident_notices", schemaName);

    public Task<IReadOnlyList<AiIncidentNotice>> ListPendingIncidentAsync(int limit, CancellationToken cancellationToken) =>
        ListPendingNoticesAsync(new SpendNoticeQuery<AiIncidentNotice>($"""
            SELECT deployment_id, authority_scope, episode_id, charged_tokens, charged_money, token_limit, money_limit
            FROM {_incidentNotices} WHERE delivered_at IS NULL
            ORDER BY created_at, deployment_id, authority_scope, episode_id LIMIT @limit
            """, reader => new AiIncidentNotice
        {
            DeploymentId = reader.GetString(0),
            AuthorityScope = reader.GetString(1),
            EpisodeId = reader.GetGuid(2),
            ChargedTokens = reader.GetDecimal(3),
            ChargedMoney = reader.GetDecimal(4),
            TokenLimit = reader.GetInt64(5),
            MoneyLimit = reader.GetInt64(6)
        }), limit, cancellationToken);

    public async Task<bool> MarkQueuedAsync(AiIncidentNotice notice, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            UPDATE {_incidentNotices} SET delivered_at = clock_timestamp()
            WHERE deployment_id = @deployment AND authority_scope = @authority AND episode_id = @episode AND delivered_at IS NULL
            """);
        command.Parameters.AddWithValue("deployment", NpgsqlDbType.Text, notice.DeploymentId);
        command.Parameters.AddWithValue("authority", NpgsqlDbType.Text, notice.AuthorityScope);
        command.Parameters.AddWithValue("episode", NpgsqlDbType.Uuid, notice.EpisodeId);
        return await MarkNoticeQueuedAsync(command, cancellationToken).ConfigureAwait(false);
    }
}

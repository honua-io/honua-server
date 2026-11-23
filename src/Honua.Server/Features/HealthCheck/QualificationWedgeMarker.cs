// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Npgsql;

namespace Honua.Server.Features.HealthCheck;

internal interface IQualificationWedgeMarker
{
    Task<bool> ShouldWedgeAsync(string token, CancellationToken cancellationToken);
}

// The first deployment to claim a token stays wedged until ECS replaces it with a new deployment.
internal sealed class QualificationWedgeMarker(
    JobStoreDatabase database,
    Func<CancellationToken, Task<string>> readDeployment) : IQualificationWedgeMarker
{
    private static readonly TimeSpan MarkerRetention = TimeSpan.FromDays(7);

    // On conflict the no-op update returns the existing row, including one a concurrent task is
    // still inserting, so every task reads the same winning deployment ID.
    private readonly string _armOrRead = $"""
        INSERT INTO {database.Table("leases")} AS marker (lease_key, owner_id, expires_at)
        VALUES (@lease_key, @deployment, now() + @retention)
        ON CONFLICT (lease_key) DO UPDATE SET owner_id = marker.owner_id
        RETURNING marker.owner_id = @deployment
        """;

    public async Task<bool> ShouldWedgeAsync(string token, CancellationToken cancellationToken)
    {
        var deployment = await readDeployment(cancellationToken).ConfigureAwait(false);
        return await database.ExecuteAsync(async (connection, _, statementToken) =>
        {
            await using var command = new NpgsqlCommand(_armOrRead, connection);
            command.Parameters.AddWithValue("lease_key", LeaseKey(token));
            command.Parameters.AddWithValue("retention", MarkerRetention);
            command.Parameters.AddWithValue("deployment", deployment);
            return (bool)(await command.ExecuteScalarAsync(statementToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The qualification wedge marker returned no row."));
        }, StatementRepeat.SafeToRepeat, cancellationToken).ConfigureAwait(false);
    }

    internal static string LeaseKey(string token) => "qualification-fault:wedged:" + token;
}

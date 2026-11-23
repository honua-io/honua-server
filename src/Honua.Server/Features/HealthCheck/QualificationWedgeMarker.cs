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
    // On conflict the update returns the existing owner, including one a concurrent task is
    // still inserting, so every task reads the same winning deployment ID. An unbounded expiry
    // preserves the decision until the isolated qualification database is removed.
    private readonly string _armOrRead = $"""
        INSERT INTO {database.Table("leases")} AS marker (lease_key, owner_id, expires_at)
        VALUES (@lease_key, @deployment, 'infinity')
        ON CONFLICT (lease_key) DO UPDATE SET expires_at = EXCLUDED.expires_at
        RETURNING marker.owner_id = @deployment
        """;

    public async Task<bool> ShouldWedgeAsync(string token, CancellationToken cancellationToken)
    {
        var deployment = await readDeployment(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return await database.ExecuteAsync(async (connection, _, statementToken) =>
        {
            await using var command = new NpgsqlCommand(_armOrRead, connection);
            command.Parameters.AddWithValue("lease_key", LeaseKey(token));
            command.Parameters.AddWithValue("deployment", deployment);
            return (bool)(await command.ExecuteScalarAsync(statementToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The qualification wedge marker returned no row."));
        }, StatementRepeat.SafeToRepeat, cancellationToken).ConfigureAwait(false);
    }

    internal static string LeaseKey(string token) => "qualification-fault:wedged:" + token;
}

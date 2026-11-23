// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Npgsql;

namespace Honua.Server.Features.HealthCheck;

internal interface IQualificationWedgeMarker
{
    Task<bool> ShouldWedgeAsync(string token, CancellationToken cancellationToken);
}

// Makes a wedge one-shot across every task of a deployment. The first task to check a token arms
// it; tasks that check within the arming window (the rest of the same rollout) wedge too. A task
// started by the recovery restart checks later and serves normally, so the restart cannot loop.
// The marker sits in the shared leases table and outlives the window so the retention sweep keeps it.
internal sealed class QualificationWedgeMarker(JobStoreDatabase database) : IQualificationWedgeMarker
{
    internal static readonly TimeSpan ArmingWindow = TimeSpan.FromSeconds(150);

    private static readonly TimeSpan MarkerRetention = TimeSpan.FromDays(7);

    // On conflict the no-op update returns the existing row, including one a concurrent task is
    // still inserting, so every task of the rollout reads the same arming time.
    private readonly string _armOrRead = $"""
        INSERT INTO {database.Table("leases")} AS marker (lease_key, owner_id, expires_at)
        VALUES (@lease_key, 'qualification-fault', now() + @retention)
        ON CONFLICT (lease_key) DO UPDATE SET owner_id = marker.owner_id
        RETURNING marker.expires_at - @retention + @window > now()
        """;

    public Task<bool> ShouldWedgeAsync(string token, CancellationToken cancellationToken)
        => database.ExecuteAsync(async (connection, _, statementToken) =>
        {
            await using var command = new NpgsqlCommand(_armOrRead, connection);
            command.Parameters.AddWithValue("lease_key", LeaseKey(token));
            command.Parameters.AddWithValue("retention", MarkerRetention);
            command.Parameters.AddWithValue("window", ArmingWindow);
            return (bool)(await command.ExecuteScalarAsync(statementToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The qualification wedge marker returned no row."));
        }, StatementRepeat.SafeToRepeat, cancellationToken);

    internal static string LeaseKey(string token) => "qualification-fault:wedged:" + token;
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.TestKit;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

internal static class GrpcGlobalIdIndex
{
    // The fixture mutates an in-memory graph, bypassing the production metadata
    // store that provisions this index when a GlobalID declaration is activated.
    public static async Task ProvisionAsync(WebAppFixture fixture, string field, int layerId = WebAppFixture.TestLayerId)
    {
        await using var connection = await fixture.Postgres.GetConnectionAsync(fixture.CurrentSchema!);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT format('CREATE UNIQUE INDEX %I ON features (((NULLIF(attributes ->> %L, %L))::uuid)) WHERE layer_id = %s',
                'idx_features_gid_' || md5(@layer::text || ':' || @field), @field, '', @layer)
            """;
        command.Parameters.AddWithValue("layer", layerId);
        command.Parameters.AddWithValue("field", field);
        command.CommandText = (string)(await command.ExecuteScalarAsync())!;
        command.Parameters.Clear();
        await command.ExecuteNonQueryAsync();
    }
}

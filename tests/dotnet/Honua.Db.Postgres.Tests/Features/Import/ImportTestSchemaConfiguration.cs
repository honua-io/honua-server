// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Db.Postgres.Features.Infrastructure;

namespace Honua.Db.Postgres.Tests.Features.Import;

/// <summary>
/// Schema layout for import tests that write into per-test isolated schemas: the provider defaults
/// plus those schemas as operational, since import targets must be operational schemas.
/// </summary>
internal static class ImportTestSchemaConfiguration
{
    public static PostgresSchemaConfiguration WithOperational(string? schema) => new(
        PostgresSchemaConfiguration.DefaultMetadataSchema,
        PostgresSchemaConfiguration.DefaultDataSchema,
        schema is null
            ? [PostgresSchemaConfiguration.DefaultDataSchema, "public"]
            : [PostgresSchemaConfiguration.DefaultDataSchema, "public", schema]);
}

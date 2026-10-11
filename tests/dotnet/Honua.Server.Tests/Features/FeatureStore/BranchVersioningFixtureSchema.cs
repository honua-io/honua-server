// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Tests.Infrastructure;

namespace Honua.Server.Tests.Features.FeatureStore;

internal static class BranchVersioningFixtureSchema
{
    public static async Task InitializeAsync(DatabaseFixtureAdapter fixture, string schema)
    {
        // Embedded migrations target the shared honua catalog, so its journal must survive
        // each test schema. Keep the feature rows and sequence isolated using the actual
        // migrated table/trigger definitions rather than replaying global migrations.
        var migration = await fixture.RunEmbeddedMigrationsUnderLockAsync("honua", typeof(Program).Assembly);
        migration.Successful.Should().BeTrue(migration.Error?.ToString());
        await fixture.ExecuteDdlUnderLockAsync($"""
            CREATE TABLE {schema}.features (LIKE honua.features INCLUDING ALL);
            CREATE SEQUENCE {schema}.features_objectid_seq OWNED BY {schema}.features.objectid;
            ALTER TABLE {schema}.features ALTER COLUMN objectid
                SET DEFAULT nextval('{schema}.features_objectid_seq'::regclass);
            DO $fixture$
            DECLARE definition text;
            BEGIN
                FOR definition IN
                    SELECT pg_get_triggerdef(oid) FROM pg_trigger
                    WHERE tgrelid = 'honua.features'::regclass AND NOT tgisinternal
                LOOP
                    EXECUTE replace(definition, ' ON honua.features ', ' ON {schema}.features ');
                END LOOP;
            END $fixture$;
            """, schema);
    }
}

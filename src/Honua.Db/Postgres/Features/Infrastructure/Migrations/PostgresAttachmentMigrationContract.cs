// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Db.Postgres.Features.Infrastructure.Migrations;

/// <summary>Exact application-owned attachment journal and resource identities supplied at composition.</summary>
internal sealed record PostgresAttachmentMigrationContract(
    string CreateMigration,
    string ImportMigration,
    string PreambleResourceName,
    string PreambleResourceSha256);

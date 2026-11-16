// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Db.Postgres.Features.Infrastructure.Migrations;

internal sealed record PostgresAttachmentMigrationContract(
    string CreateMigration,
    string ImportMigration,
    string PreambleResourceName,
    string PreambleResourceSha256);

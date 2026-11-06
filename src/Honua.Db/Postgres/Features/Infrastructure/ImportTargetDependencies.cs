// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Npgsql;

namespace Honua.Db.Postgres.Features.Infrastructure;

internal static class ImportTargetDependencies
{
    internal const string Code = "IMPORT_TARGET_HAS_DEPENDENCIES";
    internal const string Message = Code + ": Replacement was refused because other database objects depend on the target table. Existing data and dependencies were retained.";

    internal static bool IsFailure(Exception exception)
        => exception is PostgresException { SqlState: PostgresErrorCodes.DependentObjectsStillExist };
}

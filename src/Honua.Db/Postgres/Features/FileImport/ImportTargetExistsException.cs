// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Db.Postgres.Features.FileImport;

// A create-only import found its target table already present; the import reports import.target_exists.
internal sealed class ImportTargetExistsException()
    : InvalidDataException("Create-only import requires a new target table.");

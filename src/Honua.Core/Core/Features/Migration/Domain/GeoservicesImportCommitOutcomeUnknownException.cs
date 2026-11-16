// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Migration.Domain;

// A lost COMMIT acknowledgement cannot establish whether PostgreSQL persisted the rows and receipt.
public sealed class GeoservicesImportCommitOutcomeUnknownException(Exception innerException)
    : Exception("The import commit outcome must be recovered from its durable receipt.", innerException);

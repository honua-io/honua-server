// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Restore;

namespace Honua.ControlPlane;

// A response is never successful when its success flag is false.
internal static class ComposeExecutorCodes
{
    public const string ScopeViolation = "compose-scope-violation";

    public const string ConfigUnsupported = "compose-config-unsupported";

    public const string ImageNotImmutable = "compose-image-not-immutable";

    public const string ExecutorUnconfigured = "compose-executor-unconfigured";

    public const string ApprovalRequired = "compose-approval-required";

    // The backup bytes do not match the manifest checksum, and nothing was stopped.
    public const string BackupChecksumMismatch = PreMigrationRestoreCodes.BackupChecksumMismatch;

    public const string BackupImageMismatch = PreMigrationRestoreCodes.BackupImageMismatch;

    public const string BackupSchemaAhead = PreMigrationRestoreCodes.BackupSchemaAhead;

    public const string RestoreIncomplete = PreMigrationRestoreCodes.RestoreIncomplete;

    public const string ReadbackIncomplete = "compose-readback-incomplete";

    public const string AuditUnrecorded = "compose-audit-unrecorded";

    public const string BackupFailed = "compose-backup-failed";
}

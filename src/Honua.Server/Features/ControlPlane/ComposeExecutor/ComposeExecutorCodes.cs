// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Restore;

namespace Honua.ControlPlane;

// An Ok response still fails unless live readback completed.
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

    internal static bool IsReportable(string code)
        => code is ScopeViolation or ConfigUnsupported or ImageNotImmutable or ExecutorUnconfigured
            or ApprovalRequired or BackupChecksumMismatch or BackupImageMismatch or BackupSchemaAhead
            or RestoreIncomplete or ReadbackIncomplete or AuditUnrecorded or BackupFailed;
}

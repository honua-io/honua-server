// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.ControlPlane;

/// <summary>
/// Stable failure codes for the Compose proposal executor (honua-server#6811). A response is never
/// successful when <see cref="ComposeExecutorResponse.ReadbackComplete"/> is false, and these codes
/// are what an operator sees in the workflow phase.
/// </summary>
internal static class ComposeExecutorCodes
{
    /// <summary>The request tried to name a project, command, or path outside the registered project.</summary>
    public const string ScopeViolation = "compose-scope-violation";

    /// <summary>The request asked for a change the executor does not apply.</summary>
    public const string ConfigUnsupported = "compose-config-unsupported";

    /// <summary>The image is not an immutable digest reference.</summary>
    public const string ImageNotImmutable = "compose-image-not-immutable";

    /// <summary>The operator has not configured a usable executor, or the socket cannot be reached.</summary>
    public const string ExecutorUnconfigured = "compose-executor-unconfigured";

    /// <summary>Compose changes require a principal other than the requester.</summary>
    public const string ApprovalRequired = "compose-approval-required";

    /// <summary>The backup bytes do not match the manifest checksum. Nothing was stopped.</summary>
    public const string BackupChecksumMismatch = "compose-backup-checksum-mismatch";

    /// <summary>The backup manifest records a different image than the one requested.</summary>
    public const string BackupImageMismatch = "compose-backup-image-mismatch";

    /// <summary>The backup schema is newer than the prior image can read, or the prior image has no schema label.</summary>
    public const string BackupSchemaAhead = "compose-backup-schema-ahead";

    /// <summary>Restore did not return the database to the manifest schema. The service is left stopped.</summary>
    public const string RestoreIncomplete = "compose-restore-incomplete";

    /// <summary>Live readback did not confirm image, schema, health, and a non-empty customer response.</summary>
    public const string ReadbackIncomplete = "compose-readback-incomplete";

    /// <summary>The cutover may have happened, but the audit line was not recorded, so this is not success.</summary>
    public const string AuditUnrecorded = "compose-audit-unrecorded";

    /// <summary>A backup could not be taken. The previous service was started again when that was still possible.</summary>
    public const string BackupFailed = "compose-backup-failed";
}

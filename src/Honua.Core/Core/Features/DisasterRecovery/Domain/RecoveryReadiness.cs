// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.DisasterRecovery.Domain;

public sealed record RecoveryReadiness(
    RecoveryReadinessLevel Level,
    RecoveryObjectives Objectives,
    DateTimeOffset? LastSuccessfulBaseBackup,
    DateTimeOffset? RestorablePoint,
    TimeSpan? DataLossWindow,
    bool RecoveryPointObjectiveMet,
    DateTimeOffset AssessedAt);

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.DisasterRecovery.Domain;

public enum BackupKind
{
    [JsonStringEnumMemberName("postgres_base")]
    PostgresBase = 0,

    [JsonStringEnumMemberName("postgres_wal")]
    PostgresWal = 1
}

public enum RecoveryReadinessLevel
{
    [JsonStringEnumMemberName("not_ready")]
    NotReady = 0,

    [JsonStringEnumMemberName("at_risk")]
    AtRisk = 1,

    [JsonStringEnumMemberName("ready")]
    Ready = 2
}

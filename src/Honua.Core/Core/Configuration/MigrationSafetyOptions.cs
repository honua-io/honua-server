// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Configuration;

public enum ContractApplyPolicy
{
    Auto = 0,

    Gate = 1,
}

// With enforcement on, an unannotated contract-phase migration fails closed; fresh installs still provision fully.
public sealed record MigrationSafetyOptions
{
    private ContractApplyPolicy _contractApplyPolicy = ContractApplyPolicy.Gate;

    public const string SectionName = "Database:MigrationSafety";

    // The approval nonce is bound to the exact pending scripts and cannot authorize a later contract change.
    public const string ApproveContractMigrationsKey = "HONUA_APPROVE_CONTRACT_MIGRATIONS";

    public bool Enforce { get; set; } = true;

    // On an existing database a contract-phase migration is not applied unattended while older nodes may still be serving.
    public ContractApplyPolicy ContractApplyPolicy
    {
        get => _contractApplyPolicy;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(ContractApplyPolicy),
                    "Database:MigrationSafety:ContractApplyPolicy must be Auto or Gate.");
            }

            _contractApplyPolicy = value;
        }
    }

    // The backup command runs before contract-phase scripts and a non-zero exit applies nothing; it is configuration-only and is never writable through an admin API.
    public string? BackupCommand { get; set; }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit.Attributes;

namespace Honua.Architecture.Tests;

public sealed class OpsAutonomyProposalMigrationParityTests
{
    [ArchitectureTest]
    public void ProposalResolutionLedger_MigrationAndSeedStayMinimalAndInParity()
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var migration = File.ReadAllText(ArchitectureTestHelpers.CombinePath(
            root,
            "src",
            "Honua.Server",
            "Migrations",
            "081_CreateOpsAutonomyProposalResolutions.sql"));
        var seed = File.ReadAllText(ArchitectureTestHelpers.CombinePath(root, "tests", "seed", "server.yaml"));
        const string table = "ops_autonomy_proposal_resolutions";
        const string constraint = "CHECK (resolution IN (0, 1))";

        migration.Should().Contain(table);
        migration.Should().Contain(constraint);
        seed.Should().Contain(table);
        seed.Should().Contain(constraint);
        migration.Contains("execution_payload", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        migration.Contains("reason", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        migration.Contains("details", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
    }
}

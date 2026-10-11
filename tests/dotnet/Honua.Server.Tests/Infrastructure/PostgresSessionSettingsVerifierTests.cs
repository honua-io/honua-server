// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.Health;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Server.Features.Admin;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Infrastructure;

/// <summary>
/// Unit tests for the role-defaults verification (honua-server#5870): comparison of the
/// effective session settings with <c>Limits:Connections</c> and the exact <c>ALTER ROLE</c>
/// remediation statements.
/// </summary>
[Protocol(TestProtocols.TestQuality)]
public sealed class PostgresSessionSettingsVerifierTests
{
    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Evaluate_RoleDefaultsUnset_ReportsMismatchWithAlterRoleStatements()
    {
        // PostgreSQL's built-in defaults: every timeout disabled.
        var observed = new Dictionary<string, string?>
        {
            ["lock_timeout"] = "0",
            ["statement_timeout"] = "0",
            ["idle_in_transaction_session_timeout"] = "0",
            ["search_path"] = "\"$user\", public",
        };

        var snapshot = PostgresSessionSettingsVerifier.Evaluate("honua", observed, new ConnectionLimits(), defaultSchema: null);

        snapshot.Status.Should().Be(DatabaseSessionSettingsVerificationStatus.Mismatch);
        snapshot.Role.Should().Be("honua");
        snapshot.Drift.Select(drift => drift.Remediation).Should().Equal(
            "ALTER ROLE \"honua\" SET lock_timeout = '30s';",
            "ALTER ROLE \"honua\" SET statement_timeout = '30s';",
            "ALTER ROLE \"honua\" SET idle_in_transaction_session_timeout = '60s';");
        snapshot.Drift.Should().OnlyContain(drift => drift.Actual == "0 (disabled)");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Evaluate_RoleDefaultsMatchLimits_ReportsVerified()
    {
        var observed = new Dictionary<string, string?>
        {
            ["lock_timeout"] = "17000",
            ["statement_timeout"] = "43000",
            ["idle_in_transaction_session_timeout"] = "59000",
            ["search_path"] = "tenant, public",
        };
        var limits = new ConnectionLimits
        {
            LockTimeout = TimeSpan.FromSeconds(17),
            StatementTimeout = TimeSpan.FromSeconds(43),
            IdleInTransactionTimeout = TimeSpan.FromSeconds(59),
        };

        var snapshot = PostgresSessionSettingsVerifier.Evaluate("honua", observed, limits, defaultSchema: "tenant");

        snapshot.Status.Should().Be(DatabaseSessionSettingsVerificationStatus.Verified);
        snapshot.Drift.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Evaluate_DifferentValueAndMissingSearchPath_ReportsOnlyThoseSettings()
    {
        var observed = new Dictionary<string, string?>
        {
            ["lock_timeout"] = "30000",
            ["statement_timeout"] = "120000",
            ["idle_in_transaction_session_timeout"] = "60000",
            ["search_path"] = "\"$user\", public",
        };

        var snapshot = PostgresSessionSettingsVerifier.Evaluate("honua", observed, new ConnectionLimits(), defaultSchema: "Tenant_01");

        snapshot.Status.Should().Be(DatabaseSessionSettingsVerificationStatus.Mismatch);
        snapshot.Drift.Should().HaveCount(2);
        snapshot.Drift[0].Should().Be(new DatabaseSessionSettingDrift(
            "statement_timeout", "30s", "120s", "ALTER ROLE \"honua\" SET statement_timeout = '30s';"));
        snapshot.Drift[1].Setting.Should().Be("search_path");
        snapshot.Drift[1].Remediation.Should().Be("ALTER ROLE \"honua\" SET search_path = \"Tenant_01\", public;");
    }

    [UnitTheory]
    [InlineData("\"Tenant_01\", public", "Tenant_01", true)]
    [InlineData("tenant, public", "tenant", true)]
    // Unquoted identifiers fold to lower case, so an unquoted Tenant_01 is tenant_01.
    [InlineData("Tenant_01, public", "Tenant_01", false)]
    [InlineData("public, tenant", "tenant", false)]
    [InlineData("tenant", "tenant", false)]
    [InlineData("", "tenant", false)]
    [Operation(Operations.TestInfrastructure)]
    public void Evaluate_SearchPath_ComparesIdentifiersLikePostgres(string actual, string schema, bool matches)
    {
        var observed = new Dictionary<string, string?>
        {
            ["lock_timeout"] = "30000",
            ["statement_timeout"] = "30000",
            ["idle_in_transaction_session_timeout"] = "60000",
            ["search_path"] = actual,
        };

        var snapshot = PostgresSessionSettingsVerifier.Evaluate("honua", observed, new ConnectionLimits(), schema);

        snapshot.Status.Should().Be(matches
            ? DatabaseSessionSettingsVerificationStatus.Verified
            : DatabaseSessionSettingsVerificationStatus.Mismatch);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Evaluate_MissingSettingAndQuotedRole_QuotesRoleIdentifier()
    {
        var snapshot = PostgresSessionSettingsVerifier.Evaluate(
            "app\"user", new Dictionary<string, string?>(), new ConnectionLimits(), defaultSchema: null);

        snapshot.Drift.Should().HaveCount(3);
        snapshot.Drift[0].Actual.Should().BeNull();
        snapshot.Drift[0].Remediation.Should().Be("ALTER ROLE \"app\"\"user\" SET lock_timeout = '30s';");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void BuildSessionSettingsView_ReportsModeVerificationAndRemediation()
    {
        var snapshot = PostgresSessionSettingsVerifier.Evaluate(
            "honua",
            new Dictionary<string, string?> { ["lock_timeout"] = "0", ["statement_timeout"] = "30000", ["idle_in_transaction_session_timeout"] = "60000" },
            new ConnectionLimits(),
            defaultSchema: null);

        var view = DeployControlEndpoints.BuildSessionSettingsView(snapshot);

        view.Mode.Should().Be("role-defaults");
        view.Verification.Should().Be("mismatch");
        view.Role.Should().Be("honua");
        view.Remediation.Should().Equal("ALTER ROLE \"honua\" SET lock_timeout = '30s';");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void BuildSessionSettingsView_ConnectionMode_IsNotApplicable()
    {
        var state = new DatabaseSessionSettingsState(DatabaseSessionSettingsMode.Connection);

        var view = DeployControlEndpoints.BuildSessionSettingsView(state.Snapshot);

        view.Mode.Should().Be("connection");
        view.Verification.Should().Be("not-applicable");
        view.Remediation.Should().BeEmpty();
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.Health;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;

namespace Honua.Core.Tests.Configuration;

/// <summary>
/// Parsing and validation of <c>Database:SessionSettings:Mode</c> (honua-server#5870).
/// </summary>
public sealed class DatabaseSessionSettingsOptionsTests
{
    [UnitTheory]
    [InlineData(null, DatabaseSessionSettingsMode.Connection)]
    [InlineData("", DatabaseSessionSettingsMode.Connection)]
    [InlineData("   ", DatabaseSessionSettingsMode.Connection)]
    [InlineData("connection", DatabaseSessionSettingsMode.Connection)]
    [InlineData("CONNECTION", DatabaseSessionSettingsMode.Connection)]
    [InlineData("role-defaults", DatabaseSessionSettingsMode.RoleDefaults)]
    [InlineData(" Role-Defaults ", DatabaseSessionSettingsMode.RoleDefaults)]
    public void TryParseMode_RecognizedValue_ParsesMode(string? value, DatabaseSessionSettingsMode expected)
    {
        DatabaseSessionSettingsOptions.TryParseMode(value, out var mode).Should().BeTrue();
        mode.Should().Be(expected);
    }

    [UnitTheory]
    [InlineData("role_defaults")]
    [InlineData("roledefaults")]
    [InlineData("role")]
    [InlineData("none")]
    public void TryParseMode_UnrecognizedValue_ReturnsFalse(string value)
    {
        DatabaseSessionSettingsOptions.TryParseMode(value, out var mode).Should().BeFalse();
        mode.Should().Be(DatabaseSessionSettingsMode.Connection);
    }

    [UnitTheory]
    [InlineData(null, DatabaseSessionSettingsMode.Connection)]
    [InlineData("role-defaults", DatabaseSessionSettingsMode.RoleDefaults)]
    // A typo must keep today's per-connection SETs rather than silently dropping the
    // timeouts; the validator rejects it at startup.
    [InlineData("role_defaults", DatabaseSessionSettingsMode.Connection)]
    public void ResolveMode_ReadsDatabaseSessionSettingsMode(string? value, DatabaseSessionSettingsMode expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:SessionSettings:Mode"] = value })
            .Build();

        DatabaseSessionSettingsOptions.ResolveMode(configuration).Should().Be(expected);
    }

    [UnitTheory]
    [InlineData("connection")]
    [InlineData("role-defaults")]
    [InlineData("ROLE-DEFAULTS")]
    [InlineData("")]
    public void Validator_RecognizedMode_Succeeds(string mode)
    {
        var result = new DatabaseSessionSettingsOptionsValidator()
            .Validate(name: null, new DatabaseSessionSettingsOptions { Mode = mode });

        result.Succeeded.Should().BeTrue();
    }

    [UnitTest]
    public void Validator_UnrecognizedMode_FailsWithAllowedValues()
    {
        var result = new DatabaseSessionSettingsOptionsValidator()
            .Validate(name: null, new DatabaseSessionSettingsOptions { Mode = "role_defaults" });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("Database:SessionSettings:Mode")
            .And.Contain("connection")
            .And.Contain("role-defaults");
    }

    [UnitTest]
    public void Defaults_AreConnectionMode()
    {
        new DatabaseSessionSettingsOptions().Mode.Should().Be("connection");
        DatabaseSessionSettingsOptions.ToConfigurationValue(DatabaseSessionSettingsMode.Connection).Should().Be("connection");
        DatabaseSessionSettingsOptions.ToConfigurationValue(DatabaseSessionSettingsMode.RoleDefaults).Should().Be("role-defaults");
    }

    [UnitTheory]
    [InlineData(DatabaseSessionSettingsMode.Connection, DatabaseSessionSettingsVerificationStatus.NotApplicable)]
    [InlineData(DatabaseSessionSettingsMode.RoleDefaults, DatabaseSessionSettingsVerificationStatus.Pending)]
    public void State_InitialStatusFollowsMode(DatabaseSessionSettingsMode mode, DatabaseSessionSettingsVerificationStatus expected)
    {
        var state = new DatabaseSessionSettingsState(mode);

        state.Mode.Should().Be(mode);
        state.Snapshot.Status.Should().Be(expected);
    }

    [UnitTest]
    public void State_SetSnapshot_KeepsConfiguredMode()
    {
        var state = new DatabaseSessionSettingsState(DatabaseSessionSettingsMode.RoleDefaults);

        state.SetSnapshot(new DatabaseSessionSettingsSnapshot
        {
            Mode = DatabaseSessionSettingsMode.Connection,
            Status = DatabaseSessionSettingsVerificationStatus.Verified,
            Role = "honua",
        });

        state.Snapshot.Mode.Should().Be(DatabaseSessionSettingsMode.RoleDefaults);
        state.Snapshot.Status.Should().Be(DatabaseSessionSettingsVerificationStatus.Verified);
        state.Snapshot.Role.Should().Be("honua");
    }
}

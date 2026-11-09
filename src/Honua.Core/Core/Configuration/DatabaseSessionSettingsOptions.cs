// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Configuration;

namespace Honua.Core.Configuration;

public enum DatabaseSessionSettingsMode
{
    Connection,

    RoleDefaults,
}

// In role-defaults mode the server verifies the role settings at startup and never runs ALTER ROLE itself.
public sealed record DatabaseSessionSettingsOptions
{
    public const string SectionName = "Database:SessionSettings";

    public const string ConnectionModeValue = "connection";

    public const string RoleDefaultsModeValue = "role-defaults";

    public string Mode { get; set; } = ConnectionModeValue;

    public static bool TryParseMode(string? value, out DatabaseSessionSettingsMode mode)
    {
        mode = DatabaseSessionSettingsMode.Connection;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var trimmed = value.Trim();
        if (string.Equals(trimmed, ConnectionModeValue, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(trimmed, RoleDefaultsModeValue, StringComparison.OrdinalIgnoreCase))
        {
            mode = DatabaseSessionSettingsMode.RoleDefaults;
            return true;
        }

        return false;
    }

    // An unrecognized mode falls back to connection so a typo cannot silently drop the timeouts.
    public static DatabaseSessionSettingsMode ResolveMode(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return TryParseMode(configuration[$"{SectionName}:{nameof(Mode)}"], out var mode)
            ? mode
            : DatabaseSessionSettingsMode.Connection;
    }

    public static string ToConfigurationValue(DatabaseSessionSettingsMode mode)
        => mode == DatabaseSessionSettingsMode.RoleDefaults ? RoleDefaultsModeValue : ConnectionModeValue;
}

public sealed class DatabaseSessionSettingsOptionsValidator : OptionsValidator<DatabaseSessionSettingsOptions>
{
    protected override void ValidateOptions(DatabaseSessionSettingsOptions options, List<string> failures)
    {
        if (!DatabaseSessionSettingsOptions.TryParseMode(options.Mode, out _))
        {
            failures.Add(
                $"{DatabaseSessionSettingsOptions.SectionName}:Mode has an invalid value. Allowed values: " +
                $"{DatabaseSessionSettingsOptions.ConnectionModeValue}, {DatabaseSessionSettingsOptions.RoleDefaultsModeValue}.");
        }
    }
}

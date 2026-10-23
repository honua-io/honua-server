// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Configuration;

namespace Honua.Core.Configuration;

/// <summary>
/// How the PostgreSQL data source applies the per-session settings (lock, statement and
/// idle-in-transaction timeouts, and the default <c>search_path</c> from <c>Database:Schema</c>).
/// </summary>
public enum DatabaseSessionSettingsMode
{
    /// <summary>
    /// The server sets the session settings on every new physical connection (default).
    /// </summary>
    Connection,

    /// <summary>
    /// The server issues no session <c>SET</c> and relies on the database role's defaults
    /// (<c>ALTER ROLE ... SET ...</c>), so a connection proxy such as Amazon RDS Proxy can
    /// multiplex instead of pinning each client connection.
    /// </summary>
    RoleDefaults,
}

/// <summary>
/// Options for <c>Database:SessionSettings</c>.
/// </summary>
/// <remarks>
/// <c>Database:SessionSettings:Mode</c> (env var <c>Database__SessionSettings__Mode</c>) accepts
/// <c>connection</c> (default) or <c>role-defaults</c>. In <c>role-defaults</c> mode the operator
/// sets the values once on the database role; the server verifies them at startup and logs the
/// <c>ALTER ROLE</c> statements to run when they are missing or differ from
/// <c>Limits:Connections</c>. The server never runs <c>ALTER ROLE</c> itself.
/// </remarks>
public sealed record DatabaseSessionSettingsOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Database:SessionSettings";

    /// <summary>Configuration value for <see cref="DatabaseSessionSettingsMode.Connection"/>.</summary>
    public const string ConnectionModeValue = "connection";

    /// <summary>Configuration value for <see cref="DatabaseSessionSettingsMode.RoleDefaults"/>.</summary>
    public const string RoleDefaultsModeValue = "role-defaults";

    /// <summary>
    /// The session-settings mode: <c>connection</c> (default) or <c>role-defaults</c>
    /// (case-insensitive).
    /// </summary>
    public string Mode { get; set; } = ConnectionModeValue;

    /// <summary>
    /// Parses a configured mode. Null or blank means the default (<c>connection</c>).
    /// </summary>
    /// <param name="value">The configured value.</param>
    /// <param name="mode">The parsed mode.</param>
    /// <returns><see langword="true"/> when the value is recognized.</returns>
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

    /// <summary>
    /// Resolves the configured mode. Unrecognized values fall back to
    /// <see cref="DatabaseSessionSettingsMode.Connection"/> (today's behaviour, so a typo can never
    /// silently drop the timeouts); <see cref="DatabaseSessionSettingsOptionsValidator"/> rejects
    /// them at startup as the paired fail-fast guard.
    /// </summary>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The effective mode.</returns>
    public static DatabaseSessionSettingsMode ResolveMode(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return TryParseMode(configuration[$"{SectionName}:{nameof(Mode)}"], out var mode)
            ? mode
            : DatabaseSessionSettingsMode.Connection;
    }

    /// <summary>
    /// Returns the configuration value for a mode (<c>connection</c> or <c>role-defaults</c>).
    /// </summary>
    /// <param name="mode">The mode.</param>
    /// <returns>The configuration value.</returns>
    public static string ToConfigurationValue(DatabaseSessionSettingsMode mode)
        => mode == DatabaseSessionSettingsMode.RoleDefaults ? RoleDefaultsModeValue : ConnectionModeValue;
}

/// <summary>
/// Rejects unrecognized <c>Database:SessionSettings:Mode</c> values at startup.
/// </summary>
public sealed class DatabaseSessionSettingsOptionsValidator : OptionsValidator<DatabaseSessionSettingsOptions>
{
    /// <inheritdoc />
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

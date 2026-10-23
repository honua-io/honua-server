// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.Health;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Honua.Db.Postgres.Features.Infrastructure;

/// <summary>
/// In <c>Database:SessionSettings:Mode=role-defaults</c>, reads the effective session settings
/// once on a fresh connection at startup and warns (never fails) when the database role's
/// defaults are unset or differ from <c>Limits:Connections</c>, naming the exact
/// <c>ALTER ROLE</c> statements to run. The server never runs <c>ALTER ROLE</c> itself
/// (honua-server#5870).
/// </summary>
internal sealed class PostgresSessionSettingsVerificationService(
    Func<NpgsqlDataSource> dataSourceFactory,
    ConnectionLimits limits,
    string? defaultSchema,
    DatabaseSessionSettingsState state,
    ILogger<PostgresSessionSettingsVerificationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (state.Mode != DatabaseSessionSettingsMode.RoleDefaults)
        {
            return;
        }

        DatabaseSessionSettingsSnapshot snapshot;
        try
        {
            var dataSource = dataSourceFactory();
            snapshot = await PostgresSessionSettingsVerifier.VerifyAsync(
                dataSource, limits, defaultSchema, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        // Intentionally broad: verification is advisory and must never fail startup.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PostgresSessionSettingsVerificationLog.VerificationUnavailable(logger, ex);
            state.SetSnapshot(new DatabaseSessionSettingsSnapshot
            {
                Mode = DatabaseSessionSettingsMode.RoleDefaults,
                Status = DatabaseSessionSettingsVerificationStatus.Unavailable,
                Message = "The session settings could not be read from the database.",
            });
            return;
        }

        state.SetSnapshot(snapshot);
        PostgresSessionSettingsVerifier.Log(logger, snapshot);
    }
}

/// <summary>
/// Reads and compares the effective session settings against the configured limits.
/// </summary>
internal static class PostgresSessionSettingsVerifier
{
    internal const string LockTimeout = "lock_timeout";
    internal const string StatementTimeout = "statement_timeout";
    internal const string IdleInTransactionSessionTimeout = "idle_in_transaction_session_timeout";
    internal const string SearchPath = "search_path";

    private static readonly string[] SettingNames =
        [LockTimeout, StatementTimeout, IdleInTransactionSessionTimeout, SearchPath];

    /// <summary>
    /// Opens one connection, reads <c>current_user</c> and the settings from
    /// <c>pg_settings</c> (timeouts in milliseconds), and evaluates them. The query issues no
    /// <c>SET</c>, so it does not pin a proxied connection.
    /// </summary>
    public static async Task<DatabaseSessionSettingsSnapshot> VerifyAsync(
        NpgsqlDataSource dataSource,
        ConnectionLimits limits,
        string? defaultSchema,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT current_user::text, s.name, s.setting FROM pg_catalog.pg_settings s WHERE s.name = ANY(@names)";
        command.Parameters.AddWithValue("names", SettingNames);

        var role = string.Empty;
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                role = reader.GetString(0);
                settings[reader.GetString(1)] = reader.IsDBNull(2) ? null : reader.GetString(2);
            }
        }

        return Evaluate(role, settings, limits, defaultSchema);
    }

    /// <summary>
    /// Compares observed settings with the values <c>connection</c> mode would set. Timeouts
    /// are compared in whole seconds (the same truncation <c>connection</c> mode applies);
    /// <c>search_path</c> is checked only when <c>Database:Schema</c> is configured.
    /// </summary>
    /// <param name="role">The database role (<c>current_user</c>).</param>
    /// <param name="observed">Setting name to raw <c>pg_settings.setting</c> value.</param>
    /// <param name="limits">The configured connection limits.</param>
    /// <param name="defaultSchema">The configured <c>Database:Schema</c>, if any.</param>
    /// <returns>The verification snapshot.</returns>
    public static DatabaseSessionSettingsSnapshot Evaluate(
        string role,
        IReadOnlyDictionary<string, string?> observed,
        ConnectionLimits limits,
        string? defaultSchema)
    {
        ArgumentNullException.ThrowIfNull(observed);
        ArgumentNullException.ThrowIfNull(limits);

        var drift = new List<DatabaseSessionSettingDrift>();
        CheckTimeout(role, observed, LockTimeout, limits.LockTimeout, drift);
        CheckTimeout(role, observed, StatementTimeout, limits.StatementTimeout, drift);
        CheckTimeout(role, observed, IdleInTransactionSessionTimeout, limits.IdleInTransactionTimeout, drift);

        var searchPathSchema = PostgresDataSourceFactory.ResolveSearchPathSchema(schemaHeadersEnabled: false, defaultSchema);
        if (searchPathSchema is not null)
        {
            observed.TryGetValue(SearchPath, out var actual);
            if (!SearchPathMatches(actual, searchPathSchema))
            {
                var expected = $"{QuoteIdentifier(searchPathSchema)}, public";
                drift.Add(new DatabaseSessionSettingDrift(
                    SearchPath,
                    expected,
                    actual,
                    $"ALTER ROLE {QuoteIdentifier(role)} SET search_path = {expected};"));
            }
        }

        return new DatabaseSessionSettingsSnapshot
        {
            Mode = DatabaseSessionSettingsMode.RoleDefaults,
            Status = drift.Count == 0
                ? DatabaseSessionSettingsVerificationStatus.Verified
                : DatabaseSessionSettingsVerificationStatus.Mismatch,
            Role = role,
            Drift = drift,
        };
    }

    /// <summary>
    /// Logs a verification snapshot: one warning per drifted setting, or one information line.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="snapshot">The snapshot.</param>
    public static void Log(ILogger logger, DatabaseSessionSettingsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.Status == DatabaseSessionSettingsVerificationStatus.Verified)
        {
            PostgresSessionSettingsVerificationLog.Verified(logger, snapshot.Role ?? string.Empty);
            return;
        }

        foreach (var item in snapshot.Drift)
        {
            PostgresSessionSettingsVerificationLog.SettingDrift(
                logger, item.Setting, snapshot.Role ?? string.Empty, item.Actual ?? "(unset)", item.Expected, item.Remediation);
        }
    }

    internal static string QuoteIdentifier(string identifier)
        => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static void CheckTimeout(
        string role,
        IReadOnlyDictionary<string, string?> observed,
        string setting,
        TimeSpan configured,
        List<DatabaseSessionSettingDrift> drift)
    {
        var expectedSeconds = (long)configured.TotalSeconds;
        observed.TryGetValue(setting, out var actual);
        if (long.TryParse(actual, NumberStyles.Integer, CultureInfo.InvariantCulture, out var actualMilliseconds) &&
            actualMilliseconds == expectedSeconds * 1000)
        {
            return;
        }

        var expected = string.Create(CultureInfo.InvariantCulture, $"{expectedSeconds}s");
        drift.Add(new DatabaseSessionSettingDrift(
            setting,
            expected,
            actual is null ? null : FormatMilliseconds(actual),
            $"ALTER ROLE {QuoteIdentifier(role)} SET {setting} = '{expected}';"));
    }

    private static string FormatMilliseconds(string raw)
    {
        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds))
        {
            return raw;
        }

        if (milliseconds == 0)
        {
            return "0 (disabled)";
        }

        return milliseconds % 1000 == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{milliseconds / 1000}s")
            : string.Create(CultureInfo.InvariantCulture, $"{milliseconds}ms");
    }

    private static bool SearchPathMatches(string? actual, string schema)
    {
        if (string.IsNullOrWhiteSpace(actual))
        {
            return false;
        }

        var parts = actual
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(Unquote)
            .ToArray();
        return parts.Length == 2 &&
            string.Equals(parts[0], schema, StringComparison.Ordinal) &&
            string.Equals(parts[1], "public", StringComparison.Ordinal);
    }

    // Unquoted identifiers in a search_path fold to lower case; quoted ones keep their case.
    private static string Unquote(string part)
        => part.Length >= 2 && part[0] == '"' && part[^1] == '"'
            ? part[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal)
            : part.ToLowerInvariant();
}

/// <summary>
/// Source-generated logging for <see cref="PostgresSessionSettingsVerificationService"/>.
/// </summary>
internal static partial class PostgresSessionSettingsVerificationLog
{
    [LoggerMessage(
        EventId = 5870,
        Level = LogLevel.Warning,
        Message = "Database:SessionSettings:Mode=role-defaults: {Setting} for role {Role} is {Actual}, expected {Expected}. The server issues no session SET in this mode; run as an administrator: {Remediation}")]
    public static partial void SettingDrift(
        ILogger logger, string setting, string role, string actual, string expected, string remediation);

    [LoggerMessage(
        EventId = 5871,
        Level = LogLevel.Information,
        Message = "Database:SessionSettings:Mode=role-defaults: the session settings of role {Role} match Limits:Connections")]
    public static partial void Verified(ILogger logger, string role);

    [LoggerMessage(
        EventId = 5872,
        Level = LogLevel.Warning,
        Message = "Database:SessionSettings:Mode=role-defaults: the session settings could not be verified at startup")]
    public static partial void VerificationUnavailable(ILogger logger, Exception exception);
}

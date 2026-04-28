// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;

namespace Honua.DuckDB.Features.Infrastructure;

/// <summary>
/// Builds SQL fragments and bootstrap commands for DuckDB external file sources.
/// </summary>
internal static class DuckDBExternalSourceSql
{
    public static IReadOnlyList<string> GetConfiguredPaths(DuckDBExternalSourceOptions source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.Paths is { Length: > 0 })
        {
            return source.Paths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToArray();
        }

        return string.IsNullOrWhiteSpace(source.Path)
            ? []
            : [source.Path];
    }

    public static ImmutableArray<string> GetRequiredExtensions(DuckDBOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var builder = ImmutableArray.CreateBuilder<string>();
        if (RequiresHttpFs(options))
        {
            builder.Add("httpfs");
        }

        if (RequiresAzure(options))
        {
            builder.Add("azure");
        }

        return builder.ToImmutable();
    }

    public static ImmutableArray<DuckDBBootstrapSqlCommand> BuildConnectionSettingCommands(
        DuckDBOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var commands = ImmutableArray.CreateBuilder<DuckDBBootstrapSqlCommand>();
        AddS3Setting(commands, "s3_region", options.HttpFs.S3.Region, "configure S3 region");
        AddS3Setting(commands, "s3_endpoint", options.HttpFs.S3.Endpoint, "configure S3 endpoint");
        AddS3Setting(commands, "s3_access_key_id", options.HttpFs.S3.AccessKeyId, "configure S3 access key id");
        AddS3Setting(commands, "s3_secret_access_key", options.HttpFs.S3.SecretAccessKey, "configure S3 secret access key");
        AddS3Setting(commands, "s3_session_token", options.HttpFs.S3.SessionToken, "configure S3 session token");
        AddS3Setting(commands, "s3_url_style", options.HttpFs.S3.UrlStyle, "configure S3 URL style");
        AddBooleanSetting(commands, "s3_use_ssl", options.HttpFs.S3.UseSsl, "configure S3 SSL setting");
        AddBooleanSetting(commands, "s3_requester_pays", options.HttpFs.S3.RequesterPays, "configure S3 requester-pays setting");

        AddAzureSetting(commands, "azure_storage_connection_string", options.Azure.ConnectionString, "configure Azure storage connection string");
        AddAzureSetting(commands, "azure_account_name", options.Azure.AccountName, "configure Azure account name");
        AddAzureSetting(commands, "azure_endpoint", options.Azure.Endpoint, "configure Azure endpoint");
        AddAzureSetting(commands, "azure_credential_chain", options.Azure.CredentialChain, "configure Azure credential chain");
        return commands.ToImmutable();
    }

    public static ImmutableArray<DuckDBExternalSourcePlan> BuildExternalSourcePlans(
        IEnumerable<DuckDBLayerOptions> layers)
    {
        ArgumentNullException.ThrowIfNull(layers);

        var plans = ImmutableArray.CreateBuilder<DuckDBExternalSourcePlan>();
        foreach (var layer in layers)
        {
            if (layer.ExternalSource is null)
            {
                continue;
            }

            var sql = BuildCreateTempViewSql(layer);
            var requiredExtensions = ImmutableArray.CreateBuilder<string>();
            if (DuckDBOptionsValidator.UsesHttpFs(layer.ExternalSource))
            {
                requiredExtensions.Add("httpfs");
            }

            if (DuckDBOptionsValidator.UsesAzure(layer.ExternalSource))
            {
                requiredExtensions.Add("azure");
            }

            plans.Add(new DuckDBExternalSourcePlan(
                layer.Id,
                layer.Table,
                sql,
                requiredExtensions.ToImmutable()));
        }

        return plans.ToImmutable();
    }

    public static string BuildCreateTempViewSql(DuckDBLayerOptions layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        if (layer.ExternalSource is null)
        {
            throw new ArgumentException("Layer does not configure an external source.", nameof(layer));
        }

        var viewName = QuoteIdentifier(layer.Table);
        var scanExpression = BuildReadParquetExpression(layer.ExternalSource);
        return FormattableString.Invariant(
            $"CREATE OR REPLACE TEMP VIEW {viewName} AS SELECT * FROM {scanExpression}");
    }

    public static string BuildReadParquetExpression(DuckDBExternalSourceOptions source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var paths = GetConfiguredPaths(source);
        if (paths.Count == 0)
        {
            throw new ArgumentException("External source must configure Path or Paths.", nameof(source));
        }

        var arguments = new List<string>
        {
            BuildPathArgument(paths)
        };

        if (source.HivePartitioning)
        {
            arguments.Add("hive_partitioning = true");
        }

        if (source.UnionByName)
        {
            arguments.Add("union_by_name = true");
        }

        return FormattableString.Invariant($"read_parquet({string.Join(", ", arguments)})");
    }

    public static string QuoteIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw new ArgumentException("Identifier cannot be empty.", nameof(identifier));
        }

        return "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    public static string QuoteLiteral(string value)
        => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string BuildPathArgument(IReadOnlyList<string> paths)
    {
        if (paths.Count == 1)
        {
            return QuoteLiteral(paths[0]);
        }

        return "[" + string.Join(", ", paths.Select(QuoteLiteral)) + "]";
    }

    private static bool RequiresHttpFs(DuckDBOptions options)
    {
        if (HasS3Configuration(options.HttpFs.S3))
        {
            return true;
        }

        return options.Layers.Any(layer =>
            layer.ExternalSource is not null && DuckDBOptionsValidator.UsesHttpFs(layer.ExternalSource));
    }

    private static bool RequiresAzure(DuckDBOptions options)
    {
        if (HasAzureConfiguration(options.Azure))
        {
            return true;
        }

        return options.Layers.Any(layer =>
            layer.ExternalSource is not null && DuckDBOptionsValidator.UsesAzure(layer.ExternalSource));
    }

    private static bool HasS3Configuration(DuckDBS3Options options)
        => !string.IsNullOrWhiteSpace(options.Region) ||
           !string.IsNullOrWhiteSpace(options.Endpoint) ||
           !string.IsNullOrWhiteSpace(options.AccessKeyId) ||
           !string.IsNullOrWhiteSpace(options.SecretAccessKey) ||
           !string.IsNullOrWhiteSpace(options.SessionToken) ||
           !string.IsNullOrWhiteSpace(options.UrlStyle) ||
           options.UseSsl.HasValue ||
           options.RequesterPays.HasValue;

    private static bool HasAzureConfiguration(DuckDBAzureOptions options)
        => !string.IsNullOrWhiteSpace(options.ConnectionString) ||
           !string.IsNullOrWhiteSpace(options.AccountName) ||
           !string.IsNullOrWhiteSpace(options.Endpoint) ||
           !string.IsNullOrWhiteSpace(options.CredentialChain);

    private static void AddS3Setting(
        ImmutableArray<DuckDBBootstrapSqlCommand>.Builder commands,
        string settingName,
        string? value,
        string description)
        => AddStringSetting(commands, settingName, value, description);

    private static void AddAzureSetting(
        ImmutableArray<DuckDBBootstrapSqlCommand>.Builder commands,
        string settingName,
        string? value,
        string description)
        => AddStringSetting(commands, settingName, value, description);

    private static void AddStringSetting(
        ImmutableArray<DuckDBBootstrapSqlCommand>.Builder commands,
        string settingName,
        string? value,
        string description)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        commands.Add(new DuckDBBootstrapSqlCommand(
            FormattableString.Invariant($"SET {settingName} = {QuoteLiteral(value)}"),
            description));
    }

    private static void AddBooleanSetting(
        ImmutableArray<DuckDBBootstrapSqlCommand>.Builder commands,
        string settingName,
        bool? value,
        string description)
    {
        if (!value.HasValue)
        {
            return;
        }

        commands.Add(new DuckDBBootstrapSqlCommand(
            FormattableString.Invariant($"SET {settingName} = {value.Value.ToString(CultureInfo.InvariantCulture).ToLowerInvariant()}"),
            description));
    }
}

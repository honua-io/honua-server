// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.ObjectStorage;
using Honua.Core.Queries.Filters;

namespace Honua.Db.DuckDB.Features.Infrastructure;

internal sealed record DuckDBResolvedObjectStorageBinding(
    Guid ConnectionId,
    string Bucket,
    string Region,
    ImmutableArray<string> SourceUris,
    ImmutableArray<string> RequiredExtensions,
    ImmutableArray<DuckDBBootstrapSqlCommand> ConnectionSettings);

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

    public static IReadOnlyList<string> GetConfiguredObjectStorageKeys(DuckDBExternalSourceOptions source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.ObjectStorageKeys is { Length: > 0 })
        {
            return source.ObjectStorageKeys
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .ToArray();
        }

        return string.IsNullOrWhiteSpace(source.ObjectStorageKey)
            ? []
            : [source.ObjectStorageKey];
    }

    public static async Task<DuckDBResolvedObjectStorageBinding> ResolveObjectStorageBindingAsync(
        IObjectStorageConnectionResolver resolver,
        Guid connectionId,
        IReadOnlyList<string> keys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(keys);

        if (connectionId == Guid.Empty)
        {
            throw new ArgumentException("Object storage connection id must not be empty.", nameof(connectionId));
        }

        if (keys.Count == 0)
        {
            throw new ArgumentException("At least one object storage key is required.", nameof(keys));
        }

        var uris = ImmutableArray.CreateBuilder<string>(keys.Count);
        ObjectStorageConnection? connection = null;
        foreach (var key in keys)
        {
            var resolution = await resolver.ResolveObjectAsync(connectionId, key, cancellationToken).ConfigureAwait(false);
            if (!resolution.Succeeded || resolution.Connection is null)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(resolution.Message)
                        ? "Failed to resolve object storage binding for DuckDB external source."
                        : resolution.Message);
            }

            connection ??= resolution.Connection;
            DuckDBOptionsValidator.ThrowIfUnsafeSourcePath(resolution.SourceUri);
            uris.Add(resolution.SourceUri);
        }

        var settings = ImmutableArray.CreateBuilder<DuckDBBootstrapSqlCommand>();
        if (!string.IsNullOrWhiteSpace(connection!.Region))
        {
            AddStringSetting(settings, "s3_region", connection.Region, "configure S3 region from object storage connection");
        }

        return new DuckDBResolvedObjectStorageBinding(
            connection.Id,
            connection.Bucket,
            connection.Region,
            uris.ToImmutable(),
            ["spatial", "httpfs"],
            settings.ToImmutable());
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

            if (GetConfiguredPaths(layer.ExternalSource).Count == 0 && layer.ExternalSource.ObjectStorageConnectionId.HasValue)
            {
                continue;
            }

            plans.Add(BuildExternalSourcePlan(layer, resolvedPaths: null));
        }

        return plans.ToImmutable();
    }

    public static DuckDBExternalSourcePlan BuildExternalSourcePlan(
        DuckDBLayerOptions layer,
        IReadOnlyList<string>? resolvedPaths)
    {
        ArgumentNullException.ThrowIfNull(layer);
        if (layer.ExternalSource is null)
        {
            throw new ArgumentException("Layer does not configure an external source.", nameof(layer));
        }

        var effectiveSource = resolvedPaths is { Count: > 0 }
            ? new DuckDBExternalSourceOptions
            {
                Format = layer.ExternalSource.Format,
                Paths = resolvedPaths.ToArray(),
                BboxColumn = layer.ExternalSource.BboxColumn,
                HivePartitioning = layer.ExternalSource.HivePartitioning,
                UnionByName = layer.ExternalSource.UnionByName,
            }
            : layer.ExternalSource;

        var effectiveLayer = new DuckDBLayerOptions
        {
            Id = layer.Id,
            Name = layer.Name,
            Description = layer.Description,
            Table = layer.Table,
            ExternalSource = effectiveSource,
            GeometryColumn = layer.GeometryColumn,
            ObjectIdColumn = layer.ObjectIdColumn,
            Srid = layer.Srid,
            GeometryType = layer.GeometryType,
            Attributes = layer.Attributes,
        };

        var sql = BuildCreateTempViewSql(effectiveLayer);
        var requiredExtensions = ImmutableArray.CreateBuilder<string>();
        if (effectiveSource.Format.Equals("GeoParquet", StringComparison.OrdinalIgnoreCase))
        {
            requiredExtensions.Add("spatial");
        }

        if (DuckDBOptionsValidator.UsesHttpFs(effectiveSource) || layer.ExternalSource.ObjectStorageConnectionId.HasValue)
        {
            requiredExtensions.Add("httpfs");
        }

        if (DuckDBOptionsValidator.UsesAzure(effectiveSource))
        {
            requiredExtensions.Add("azure");
        }

        return new DuckDBExternalSourcePlan(
            layer.Id,
            layer.Table,
            sql,
            requiredExtensions.ToImmutable())
        {
            SourcePaths = [.. GetConfiguredPaths(effectiveSource)],
        };
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

        foreach (var path in paths)
        {
            DuckDBOptionsValidator.ThrowIfUnsafeSourcePath(path);
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

        return SqlIdentifier.QuoteIdentifier(identifier);
    }

    public static string QuoteAttribute(string identifier)
    {
        if (!FeatureFieldNameSyntax.IsValid(identifier))
        {
            throw new ArgumentException(
                FormattableString.Invariant($"Unsupported DuckDB attribute column name '{identifier}'."),
                nameof(identifier));
        }

        return QuoteIdentifier(identifier);
    }

    public static bool IsSupportedAttributeName(string? identifier)
        => FeatureFieldNameSyntax.IsValid(identifier);

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
            FormattableString.Invariant($"SET GLOBAL {settingName} = {QuoteLiteral(value)}"),
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
            FormattableString.Invariant($"SET GLOBAL {settingName} = {value.Value.ToString(CultureInfo.InvariantCulture).ToLowerInvariant()}"),
            description));
    }
}

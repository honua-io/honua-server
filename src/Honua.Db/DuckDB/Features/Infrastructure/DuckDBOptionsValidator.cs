// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;

namespace Honua.DuckDB.Features.Infrastructure;

/// <summary>
/// Validates DuckDB provider configuration before the provider starts serving requests.
/// </summary>
internal static class DuckDBOptionsValidator
{
    private static readonly HashSet<string> SupportedFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "Parquet",
        "GeoParquet"
    };

    private static readonly HashSet<string> HttpFsSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http",
        "https",
        "s3",
        "s3a",
        "s3n",
        "gs",
        "gcs",
        "r2"
    };

    private static readonly HashSet<string> AzureSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "az",
        "azure",
        "abfs",
        "abfss",
        "wasb",
        "wasbs"
    };

    private static readonly HashSet<string> AllowedLocalSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "file"
    };

    private static readonly string[] SensitiveQueryKeys =
    [
        "access_key",
        "accesskey",
        "access-key",
        "secret",
        "token",
        "signature",
        "credential",
        "sig="
    ];

    public static IReadOnlyList<string> Validate(DuckDBOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var errors = new List<string>();
        ValidateLayers(options, errors);
        ValidateObjectStoreOptions(options, errors);
        return errors;
    }

    public static void ThrowIfInvalid(DuckDBOptions options)
    {
        var errors = Validate(options);
        if (errors.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            "Invalid DuckDB configuration: " + string.Join("; ", errors));
    }

    private static void ValidateLayers(DuckDBOptions options, List<string> errors)
    {
        var layerIds = new HashSet<int>();
        foreach (var layer in options.Layers)
        {
            if (!layerIds.Add(layer.Id))
            {
                errors.Add(FormattableString.Invariant($"Layer id {layer.Id} is configured more than once."));
            }

            if (string.IsNullOrWhiteSpace(layer.Table))
            {
                errors.Add(FormattableString.Invariant($"Layer {layer.Id} must configure DuckDB:Layers:N:Table."));
            }

            if (string.IsNullOrWhiteSpace(layer.GeometryColumn))
            {
                errors.Add(FormattableString.Invariant($"Layer {layer.Id} must configure a geometry column."));
            }

            if (string.IsNullOrWhiteSpace(layer.ObjectIdColumn))
            {
                errors.Add(FormattableString.Invariant($"Layer {layer.Id} must configure an object ID column."));
            }

            if (layer.ExternalSource is not null)
            {
                ValidateExternalSource(layer.Id, layer.ExternalSource, errors);
            }
        }
    }

    private static void ValidateExternalSource(
        int layerId,
        DuckDBExternalSourceOptions source,
        List<string> errors)
    {
        if (!string.IsNullOrWhiteSpace(source.Path) && source.Paths is { Length: > 0 })
        {
            errors.Add(FormattableString.Invariant(
                $"Layer {layerId} external source must configure either Path or Paths, not both."));
        }

        if (!SupportedFormats.Contains(source.Format))
        {
            errors.Add(FormattableString.Invariant(
                $"Layer {layerId} external source format '{source.Format}' is not supported. Use Parquet or GeoParquet."));
        }

        if (!string.IsNullOrWhiteSpace(source.Path) && source.Paths is { Length: > 0 })
        {
            errors.Add(FormattableString.Invariant(
                $"Layer {layerId} external source must configure either Path or Paths, not both."));
        }

        var paths = DuckDBExternalSourceSql.GetConfiguredPaths(source);
        if (paths.Count == 0)
        {
            errors.Add(FormattableString.Invariant(
                $"Layer {layerId} external source must configure Path or Paths."));
            return;
        }

        foreach (var path in paths)
        {
            ValidateSourcePath(layerId, path, errors);
        }
    }

    private static void ValidateSourcePath(int layerId, string path, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            errors.Add(FormattableString.Invariant(
                $"Layer {layerId} external source contains an empty path."));
            return;
        }

        if (!TryGetScheme(path, out var scheme))
        {
            return;
        }

        if (!HttpFsSchemes.Contains(scheme) &&
            !AzureSchemes.Contains(scheme) &&
            !AllowedLocalSchemes.Contains(scheme))
        {
            errors.Add(FormattableString.Invariant(
                $"Layer {layerId} external source path scheme '{scheme}' is not supported."));
        }

        var queryIndex = path.IndexOf('?', StringComparison.Ordinal);
        if (queryIndex >= 0)
        {
            var query = path[(queryIndex + 1)..];
            if (SensitiveQueryKeys.Any(key => query.Contains(key, StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add(FormattableString.Invariant(
                    $"Layer {layerId} external source path must not include credential query parameters; configure credentials through DuckDB:HttpFs or DuckDB:Azure."));
            }
        }
    }

    private static void ValidateObjectStoreOptions(DuckDBOptions options, List<string> errors)
    {
        if (!string.IsNullOrWhiteSpace(options.HttpFs.S3.UrlStyle) &&
            !options.HttpFs.S3.UrlStyle.Equals("path", StringComparison.OrdinalIgnoreCase) &&
            !options.HttpFs.S3.UrlStyle.Equals("vhost", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("DuckDB:HttpFs:S3:UrlStyle must be 'path' or 'vhost'.");
        }

        if (!string.IsNullOrWhiteSpace(options.Azure.ConnectionString) &&
            !string.IsNullOrWhiteSpace(options.Azure.CredentialChain))
        {
            errors.Add("DuckDB:Azure:ConnectionString and DuckDB:Azure:CredentialChain cannot both be configured.");
        }
    }

    internal static bool UsesHttpFs(DuckDBExternalSourceOptions source)
        => DuckDBExternalSourceSql.GetConfiguredPaths(source).Any(path =>
            TryGetScheme(path, out var scheme) && HttpFsSchemes.Contains(scheme));

    internal static bool UsesAzure(DuckDBExternalSourceOptions source)
        => DuckDBExternalSourceSql.GetConfiguredPaths(source).Any(path =>
            TryGetScheme(path, out var scheme) && AzureSchemes.Contains(scheme));

    private static bool TryGetScheme(string path, out string scheme)
    {
        scheme = string.Empty;

        var schemeSeparatorIndex = path.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparatorIndex <= 0)
        {
            return false;
        }

        scheme = path[..schemeSeparatorIndex].ToLower(CultureInfo.InvariantCulture);
        return true;
    }
}

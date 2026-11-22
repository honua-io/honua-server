// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.ObjectStorage;

namespace Honua.Db.DuckDB.Features.Infrastructure;

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

    private static readonly HashSet<string> ObjectStoreBucketSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
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
        "sig=",
        "aws_access_key_id",
        "x-amz-credential",
        "x-amz-security-token",
        "x-amz-signature"
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

    public static void ThrowIfUnsafeSourcePath(string path, int layerId = 0)
    {
        var errors = new List<string>();
        ValidateSourcePath(layerId, path, errors);
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join("; ", errors), nameof(path));
        }
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
        if (!SupportedFormats.Contains(source.Format))
        {
            errors.Add(FormattableString.Invariant(
                $"Layer {layerId} external source format '{source.Format}' is not supported. Use Parquet or GeoParquet."));
        }

        if (!string.IsNullOrWhiteSpace(source.BboxColumn) && !FeatureFieldNameSyntax.IsValid(source.BboxColumn))
        {
            errors.Add(FormattableString.Invariant(
                $"Layer {layerId} external source bbox column '{source.BboxColumn}' is not a valid identifier."));
        }

        var hasDirectPaths = !string.IsNullOrWhiteSpace(source.Path) || source.Paths is { Length: > 0 };
        var hasConnectionBinding = source.ObjectStorageConnectionId.HasValue
            || !string.IsNullOrWhiteSpace(source.ObjectStorageKey)
            || source.ObjectStorageKeys is { Length: > 0 };

        if (hasDirectPaths && hasConnectionBinding)
        {
            errors.Add(FormattableString.Invariant(
                $"Layer {layerId} external source must configure either Path/Paths or ObjectStorageConnectionId, not both."));
            return;
        }

        if (hasConnectionBinding)
        {
            ValidateObjectStorageBinding(layerId, source, errors);
            return;
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

    private static void ValidateObjectStorageBinding(
        int layerId,
        DuckDBExternalSourceOptions source,
        List<string> errors)
    {
        if (!source.ObjectStorageConnectionId.HasValue || source.ObjectStorageConnectionId.Value == Guid.Empty)
        {
            errors.Add(FormattableString.Invariant(
                $"Layer {layerId} external source requires a valid ObjectStorageConnectionId."));
        }

        if (!string.IsNullOrWhiteSpace(source.ObjectStorageKey) && source.ObjectStorageKeys is { Length: > 0 })
        {
            errors.Add(FormattableString.Invariant(
                $"Layer {layerId} external source must configure either ObjectStorageKey or ObjectStorageKeys, not both."));
        }

        var keys = DuckDBExternalSourceSql.GetConfiguredObjectStorageKeys(source);
        if (keys.Count == 0)
        {
            errors.Add(FormattableString.Invariant(
                $"Layer {layerId} external source must configure ObjectStorageKey or ObjectStorageKeys when ObjectStorageConnectionId is set."));
            return;
        }

        foreach (var key in keys)
        {
            if (!ObjectStoragePolicy.IsKeyUnderPrefix(string.Empty, key) || ContainsSqlInjectionSyntax(key))
            {
                errors.Add(FormattableString.Invariant(
                    $"Layer {layerId} external source object storage key '{key}' is invalid."));
            }
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

        if (ContainsSqlInjectionSyntax(path))
        {
            errors.Add(FormattableString.Invariant(
                $"Layer {layerId} external source path contains disallowed control characters or SQL syntax."));
            return;
        }

        if (ContainsPathTraversalOrVsi(path))
        {
            errors.Add(FormattableString.Invariant(
                $"Layer {layerId} external source path must not contain path traversal segments or virtual filesystem prefixes."));
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
            return;
        }

        var authorityAndRest = path[(scheme.Length + 3)..];
        var slashIndex = authorityAndRest.IndexOf('/', StringComparison.Ordinal);
        var authority = slashIndex >= 0 ? authorityAndRest[..slashIndex] : authorityAndRest;
        if (authority.Contains('@', StringComparison.Ordinal))
        {
            errors.Add(FormattableString.Invariant(
                $"Layer {layerId} external source path must not include embedded URI credentials."));
        }

        if (ObjectStoreBucketSchemes.Contains(scheme))
        {
            ValidateObjectStoreUri(layerId, authority, slashIndex >= 0 ? authorityAndRest[(slashIndex + 1)..] : string.Empty, errors);
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

    private static void ValidateObjectStoreUri(int layerId, string bucket, string keyAndQuery, List<string> errors)
    {
        if (!ObjectStoragePolicy.IsValidBucketName(bucket))
        {
            errors.Add(FormattableString.Invariant(
                $"Layer {layerId} external source bucket '{bucket}' is not a valid object-storage bucket name."));
        }

        var queryIndex = keyAndQuery.IndexOf('?', StringComparison.Ordinal);
        var key = queryIndex >= 0 ? keyAndQuery[..queryIndex] : keyAndQuery;
        if (string.IsNullOrWhiteSpace(key))
        {
            errors.Add(FormattableString.Invariant(
                $"Layer {layerId} external source URI must include a non-empty object key."));
        }
        else if (key.Contains('\\', StringComparison.Ordinal) || key.Contains('\'', StringComparison.Ordinal))
        {
            errors.Add(FormattableString.Invariant(
                $"Layer {layerId} external source object key contains disallowed characters."));
        }
    }

    private static bool ContainsSqlInjectionSyntax(string value)
        => value.Any(char.IsControl)
            || value.Contains(';', StringComparison.Ordinal)
            || value.Contains("--", StringComparison.Ordinal);

    private static bool ContainsPathTraversalOrVsi(string value)
    {
        if (value.Contains("/vsi", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("vsi", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var queryIndex = value.IndexOf('?', StringComparison.Ordinal);
        var withoutQuery = queryIndex >= 0 ? value[..queryIndex] : value;
        var schemeIndex = withoutQuery.IndexOf("://", StringComparison.Ordinal);
        var pathPart = schemeIndex >= 0 ? withoutQuery[(schemeIndex + 3)..] : withoutQuery;
        foreach (var segment in pathPart.Split(['/', '\\']))
        {
            if (segment == "..")
            {
                return true;
            }
        }

        return false;
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
        => source.ObjectStorageConnectionId.HasValue
            || DuckDBExternalSourceSql.GetConfiguredPaths(source).Any(path =>
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

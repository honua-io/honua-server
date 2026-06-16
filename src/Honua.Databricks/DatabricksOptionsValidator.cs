// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Databricks.Features.Infrastructure;

namespace Honua.Databricks;

/// <summary>
/// Validates <see cref="DatabricksOptions"/> at startup so misconfiguration fails
/// fast rather than surfacing as opaque runtime HTTP errors.
/// </summary>
internal static class DatabricksOptionsValidator
{
    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> when the options are invalid.
    /// Only validates when the provider is enabled; a disabled provider skips
    /// connection validation so a deployment can keep the section partially filled.
    /// </summary>
    /// <param name="options">Bound options.</param>
    public static void ThrowIfInvalid(DatabricksOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(options.Host)
            || !Uri.TryCreate(options.Host, UriKind.Absolute, out var host)
            || host.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                "Databricks:Host must be an absolute HTTPS workspace URL (for example https://dbc-abc123.cloud.databricks.com).");
        }

        if (string.IsNullOrWhiteSpace(options.WarehouseId))
        {
            throw new InvalidOperationException("Databricks:WarehouseId is required.");
        }

        if (string.IsNullOrWhiteSpace(options.Token))
        {
            throw new InvalidOperationException("Databricks:Token is required (personal access token or OAuth bearer token).");
        }

        if (options.CommandTimeoutSeconds <= 0)
        {
            throw new InvalidOperationException("Databricks:CommandTimeoutSeconds must be positive.");
        }

        if (options.PollIntervalMilliseconds <= 0)
        {
            throw new InvalidOperationException("Databricks:PollIntervalMilliseconds must be positive.");
        }

        foreach (var layer in options.Layers)
        {
            DatabricksIdentifier.ValidateIdentifier(layer.Table);
            DatabricksIdentifier.ValidateIdentifier(layer.GeometryColumn);
            DatabricksIdentifier.ValidateIdentifier(layer.PrimaryKeyColumn);

            var effectiveCatalog = layer.Catalog ?? options.Catalog;
            if (!string.IsNullOrWhiteSpace(effectiveCatalog))
            {
                DatabricksIdentifier.ValidateIdentifier(effectiveCatalog);
            }

            var effectiveSchema = layer.Schema ?? options.Schema;
            if (!string.IsNullOrWhiteSpace(effectiveSchema))
            {
                DatabricksIdentifier.ValidateIdentifier(effectiveSchema);
            }

            foreach (var attribute in layer.Attributes)
            {
                DatabricksIdentifier.ValidateIdentifier(attribute);
            }

            if (!Enum.TryParse<GeometryType>(layer.GeometryType, ignoreCase: true, out _))
            {
                throw new InvalidOperationException(
                    $"Databricks layer {layer.Id} declares unknown GeometryType '{layer.GeometryType}'.");
            }
        }
    }
}

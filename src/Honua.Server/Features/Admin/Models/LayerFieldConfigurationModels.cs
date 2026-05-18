// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Request payload for updating persisted layer field configuration.
/// </summary>
public sealed class LayerFieldConfigurationUpdateRequest
{
    /// <summary>
    /// Field configuration updates to apply. Omitted fields preserve their current configuration.
    /// </summary>
    public IReadOnlyList<LayerFieldConfigurationUpdateItem> Fields { get; init; } = Array.Empty<LayerFieldConfigurationUpdateItem>();
}

/// <summary>
/// Field configuration update item.
/// </summary>
public sealed class LayerFieldConfigurationUpdateItem
{
    /// <summary>
    /// Name of the persisted layer field to update.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Optional display alias for the field. Null or blank clears the alias.
    /// </summary>
    public string? Alias { get; init; }

    /// <summary>
    /// Optional coded-value domain for the field. Null clears the domain.
    /// </summary>
    public FieldDomainDefinition? Domain { get; init; }

    /// <summary>
    /// Optional hidden-field flag. Null preserves the current value.
    /// </summary>
    public bool? Hidden { get; init; }
}

/// <summary>
/// Response payload for persisted layer field configuration.
/// </summary>
public sealed class LayerFieldConfigurationResponse
{
    /// <summary>
    /// Layer identifier.
    /// </summary>
    public int LayerId { get; init; }

    /// <summary>
    /// Field configurations ordered by field order.
    /// </summary>
    public IReadOnlyList<LayerFieldConfigurationItem> Fields { get; init; } = Array.Empty<LayerFieldConfigurationItem>();
}

/// <summary>
/// Persisted configuration for one layer field.
/// </summary>
public sealed class LayerFieldConfigurationItem
{
    /// <summary>
    /// Field name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Field data type.
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Optional display alias for the field.
    /// </summary>
    public string? Alias { get; init; }

    /// <summary>
    /// Optional coded-value domain for the field.
    /// </summary>
    public FieldDomainDefinition? Domain { get; init; }

    /// <summary>
    /// Whether public protocol output should omit the field.
    /// </summary>
    public bool Hidden { get; init; }
}

/// <summary>
/// Request payload for updating persisted layer filter configuration.
/// </summary>
public sealed class LayerFilterConfigurationUpdateRequest
{
    /// <summary>
    /// Permanent layer filter to apply to public feature output. Null clears the saved filter.
    /// </summary>
    public LayerPermanentFilterConfiguration? PermanentFilter { get; init; }
}

/// <summary>
/// Response payload for persisted layer filter configuration.
/// </summary>
public sealed class LayerFilterConfigurationResponse
{
    /// <summary>
    /// Layer identifier.
    /// </summary>
    public int LayerId { get; init; }

    /// <summary>
    /// Permanent layer filter applied to public feature output.
    /// </summary>
    public LayerPermanentFilterConfiguration? PermanentFilter { get; init; }
}

/// <summary>
/// Permanent filter contract for layer configuration APIs.
/// </summary>
public sealed class LayerPermanentFilterConfiguration
{
    /// <summary>
    /// Filter expression text in the declared language.
    /// </summary>
    public required string Expression { get; init; }

    /// <summary>
    /// Filter language token. Supported values are arcgis-sql, cql2-text, and cql2-json.
    /// </summary>
    public string Language { get; init; } = LayerPermanentFilterLanguages.ArcGisSql;
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using PermanentFilterLanguages = Honua.Core.Features.Metadata.Domain.V2.MetadataV2PermanentFilterLanguages;

namespace Honua.Server.Features.Admin.Models;

public sealed class LayerFieldConfigurationUpdateRequest
{
    public IReadOnlyList<LayerFieldConfigurationUpdateItem> Fields { get; init; } = Array.Empty<LayerFieldConfigurationUpdateItem>();
}

public sealed class LayerFieldConfigurationUpdateItem
{
    public required string Name { get; init; }

    public string? Alias { get; init; }

    public MetadataV2FieldDomain? Domain { get; init; }

    public bool? Hidden { get; init; }
}

public sealed class LayerFieldConfigurationResponse
{
    public int LayerId { get; init; }

    public IReadOnlyList<LayerFieldConfigurationItem> Fields { get; init; } = Array.Empty<LayerFieldConfigurationItem>();
}

public sealed class LayerFieldConfigurationItem
{
    public required string Name { get; init; }

    public required string Type { get; init; }

    public string? Alias { get; init; }

    public MetadataV2FieldDomain? Domain { get; init; }

    public bool Hidden { get; init; }
}

public sealed class LayerFilterConfigurationUpdateRequest
{
    public LayerPermanentFilterConfiguration? PermanentFilter { get; init; }
}

public sealed class LayerFilterConfigurationResponse
{
    public int LayerId { get; init; }

    public LayerPermanentFilterConfiguration? PermanentFilter { get; init; }
}

public sealed class LayerPermanentFilterConfiguration
{
    public required string Expression { get; init; }

    public string Language { get; init; } = PermanentFilterLanguages.GeoServicesSql;
}

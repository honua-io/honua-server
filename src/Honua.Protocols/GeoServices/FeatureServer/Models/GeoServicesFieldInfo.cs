// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Shared.Models;

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

public sealed record GeoServicesFieldInfo : FieldDefinitionBase
{
    public string? SqlType { get; init; }

    public GeoServicesFieldDomainInfo? Domain { get; init; }

    public bool Editable { get; init; } = true;

    public bool Visible { get; init; } = true;

    public bool CanSort { get; init; } = true;

    protected override bool IsStringType()
    {
        return Type.Equals("esriFieldTypeString", StringComparison.OrdinalIgnoreCase);
    }
}

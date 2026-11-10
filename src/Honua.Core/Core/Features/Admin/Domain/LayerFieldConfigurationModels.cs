// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Admin.Domain;

public sealed record LayerFieldConfiguration(
    string Name,
    string? Alias,
    MetadataV2FieldDomain? Domain,
    bool Hidden);

public sealed record LayerFieldConfigurationUpdate(
    string Name,
    string? Alias,
    MetadataV2FieldDomain? Domain,
    bool? Hidden);

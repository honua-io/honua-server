// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Serialization;

namespace Honua.Core.Features.Metadata.Domain.V2;

public sealed record MetadataV2CatalogConfiguration : ExtensibleJsonRecord
{
    private MetadataV2CatalogAudience? _audience = new();
    private MetadataV2CatalogScope? _scope;
    private IReadOnlyList<MetadataV2CatalogMembership>? _memberships;

    public MetadataV2CatalogAudience Audience { get => _audience ?? new() { Kind = "unknown" }; init => _audience = value; }
    public MetadataV2CatalogScope Scope { get => _scope ?? new(); init => _scope = value; }
    public string DefaultListing { get; init; } = "after-review";
    public IReadOnlyList<MetadataV2CatalogMembership> Memberships { get => _memberships ?? []; init => _memberships = value; }
}

public sealed record MetadataV2CatalogAudience : ExtensibleJsonRecord
{
    private IReadOnlyList<string>? _roleIds;
    public string Kind { get; init; } = "public";
    public IReadOnlyList<string> RoleIds { get => _roleIds ?? []; init => _roleIds = value; }
}

public sealed record MetadataV2CatalogScope : ExtensibleJsonRecord
{
    private IReadOnlyList<string>? _folderIds;
    private IReadOnlyList<string>? _tags;
    private IReadOnlyList<string>? _kinds;
    public IReadOnlyList<string> FolderIds { get => _folderIds ?? []; init => _folderIds = value; }
    public IReadOnlyList<string> Tags { get => _tags ?? []; init => _tags = value; }
    public IReadOnlyList<string> Kinds { get => _kinds ?? []; init => _kinds = value; }
}

public sealed record MetadataV2CatalogItemRef : ExtensibleJsonRecord
{
    public string Kind { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
}

public sealed record MetadataV2CatalogMembership : ExtensibleJsonRecord
{
    private IReadOnlyList<string>? _categoryIds;
    public MetadataV2CatalogItemRef Item { get; init; } = new();
    public string Inclusion { get; init; } = "scope";
    public bool HiddenHere { get; init; }
    public IReadOnlyList<string> CategoryIds { get => _categoryIds ?? []; init => _categoryIds = value; }
    public int? FeaturedPosition { get; init; }
    public string? ApprovedItemRevision { get; init; }
    public string? ApprovedConfigurationRevision { get; init; }
    public MetadataV2CatalogAppearance? Appearance { get; init; }
}

public sealed record MetadataV2CatalogAppearance : ExtensibleJsonRecord
{
    public MetadataV2CatalogTextOverride Title { get; init; } = new();
    public MetadataV2CatalogTextOverride Summary { get; init; } = new();
}

public sealed record MetadataV2CatalogTextOverride : ExtensibleJsonRecord
{
    public string Mode { get; init; } = "inherit";
    public string? Value { get; init; }
}

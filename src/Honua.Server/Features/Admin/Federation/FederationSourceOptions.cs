// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using Honua.Core.Features.Federation.Domain;

namespace Honua.Server.Features.Admin.Federation;

public sealed class FederationSourceOptions
{
    public const string SectionName = "Federation";

    public IReadOnlyList<FederationSourceConfig> Sources { get; set; } = Array.Empty<FederationSourceConfig>();
}

public sealed class FederationSourceConfig
{
    [Required]
    public string Id { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    public FederatedSourceKind Kind { get; set; }

    [Required]
    public string Endpoint { get; set; } = string.Empty;

    [Required]
    public string RemoteLayer { get; set; } = string.Empty;

    [Range(1, 600)]
    public int RequestTimeoutSeconds { get; set; } = 30;

    public bool Enabled { get; set; } = true;
}

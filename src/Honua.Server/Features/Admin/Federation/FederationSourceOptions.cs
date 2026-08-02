// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using Honua.Core.Features.Federation.Domain;

namespace Honua.Server.Features.Admin.Federation;

/// <summary>
/// Configuration-bound list of federated sources for the deployment (issue #341).
/// Sources are declared under the <c>Federation</c> configuration section. This is the
/// first increment of the federation source registry; a later increment can promote it to
/// the metadata-resource store (ADR-0023) without changing the read abstraction.
/// </summary>
public sealed class FederationSourceOptions
{
    /// <summary>
    /// Configuration section name for binding from environment / appsettings.
    /// </summary>
    public const string SectionName = "Federation";

    /// <summary>
    /// Gets or sets the configured federated sources.
    /// </summary>
    public IReadOnlyList<FederationSourceConfig> Sources { get; set; } = Array.Empty<FederationSourceConfig>();
}

/// <summary>
/// A single configured federated source entry.
/// </summary>
public sealed class FederationSourceConfig
{
    /// <summary>
    /// Gets the stable identifier, unique within the deployment.
    /// </summary>
    [Required]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets the human-readable display name.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Gets the transport family of the source.
    /// </summary>
    public FederatedSourceKind Kind { get; set; }

    /// <summary>
    /// Gets the base endpoint URL of the remote service.
    /// </summary>
    [Required]
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// Gets the remote layer or collection identifier exposed for federated joins.
    /// </summary>
    [Required]
    public string RemoteLayer { get; set; } = string.Empty;

    /// <summary>
    /// Gets the per-request timeout in seconds applied to remote calls. Defaults to 30s.
    /// </summary>
    [Range(1, 600)]
    public int RequestTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Gets a value indicating whether the source is enabled for federation.
    /// </summary>
    public bool Enabled { get; set; } = true;
}

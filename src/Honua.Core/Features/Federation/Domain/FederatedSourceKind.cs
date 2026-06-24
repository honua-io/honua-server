// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Federation.Domain;

/// <summary>
/// Transport family of a federated data source. The kind determines which query
/// predicates a remote source can evaluate in-place (push-down) versus which the
/// federation layer must evaluate locally after fetching candidate rows.
/// </summary>
/// <remarks>
/// See issue #341 (federated queries) and ADR-0024 (enterprise tier). The kind is a
/// pure capability discriminator; it carries no transport credentials and is safe to
/// evaluate offline, which keeps query planning deterministic and testable without a
/// live remote warehouse.
/// </remarks>
public enum FederatedSourceKind
{
    /// <summary>
    /// Another Honua instance reached over the canonical gRPC feature-service transport.
    /// Honua-to-Honua sources share the canonical filter model, so attribute, spatial,
    /// and temporal predicates all push down.
    /// </summary>
    HonuaGrpc,

    /// <summary>
    /// A remote Esri ArcGIS REST feature service reached over HTTP. The Esri REST query
    /// API accepts a SQL-like <c>where</c> clause and a spatial envelope, so attribute and
    /// spatial predicates push down but temporal interval filters are evaluated locally.
    /// </summary>
    EsriRest,

    /// <summary>
    /// A remote OGC API - Features or WFS endpoint reached over HTTP. CQL2 attribute and
    /// bbox spatial predicates push down; richer spatial relationships and ordering are
    /// evaluated locally.
    /// </summary>
    OgcWfs
}

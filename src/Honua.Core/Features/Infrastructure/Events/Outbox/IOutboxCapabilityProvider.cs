// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Events.Outbox;

/// <summary>
/// Reports whether the active database backend supports a transactional outbox
/// for feature-change CDC. Backends that do (PostgreSQL, SQL Server) route the
/// canonical event publish through <see cref="IFeatureChangeOutboxRepository"/>.
/// Backends that do not (DuckDB read-only) fall back to the existing best-effort
/// post-commit publish + retry-queue path.
/// </summary>
public interface IOutboxCapabilityProvider
{
    /// <summary>
    /// True when the backing provider can durably persist outbox rows for feature mutations.
    /// </summary>
    bool SupportsTransactionalOutbox { get; }

    /// <summary>
    /// Human-readable description of any capability limitation. Logged at startup so operators
    /// understand why streaming/CDC durability is reduced when the value is non-null.
    /// </summary>
    string? CapabilityLimitationDescription { get; }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Guardrails.Domain;

namespace Honua.Core.Features.ControlPlane.Abstractions;

/// <summary>
/// Small capability surface over the registered <see cref="IOperationExecutor"/> set, kept
/// separate from <see cref="IOperationGateway"/> so discovery surfaces (for example the MCP
/// <c>honua_propose_operation</c> tool's <c>supportedKinds</c> reporting) can ask which
/// <see cref="OperationClass"/> kinds are genuinely routable without widening the gateway's own
/// routing contract (#2563). A kind absent from <see cref="SupportedKinds"/> always resolves to
/// <see cref="OperationGatewayOutcome.NotSupported"/> when routed through
/// <see cref="IOperationGateway.RouteAsync"/> — this surface exists so callers can discover that
/// truth up front instead of hitting a dead end after proposing.
/// </summary>
public interface IOperationExecutorCatalog
{
    /// <summary>
    /// Operation classes that have a registered <see cref="IOperationExecutor"/> and are
    /// therefore genuinely routable through the gateway. Reflects the actual DI registration, not
    /// a static/declared list, so it can never drift from what the gateway will do.
    /// </summary>
    IReadOnlyCollection<OperationClass> SupportedKinds { get; }
}

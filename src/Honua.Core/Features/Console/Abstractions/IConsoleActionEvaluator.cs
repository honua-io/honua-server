// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Console.Domain;

namespace Honua.Core.Features.Console.Abstractions;

/// <summary>
/// Server-authored evaluator that resolves which Console actions a principal may
/// perform on a content item or navigation route. Implementations bulk-evaluate
/// over a page to avoid per-item authorization round trips.
/// </summary>
public interface IConsoleActionEvaluator
{
    /// <summary>
    /// Evaluates actions for one item.
    /// </summary>
    Task<IReadOnlyList<ConsoleContentAction>> EvaluateItemActionsAsync(
        ClaimsPrincipal principal,
        ConsoleContentItem item,
        IReadOnlyList<ConsoleContentAction> candidateActions,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Evaluates actions for a page of items in a single pass.
    /// </summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<ConsoleContentAction>>> EvaluateItemActionsAsync(
        ClaimsPrincipal principal,
        IReadOnlyList<ConsoleContentItem> items,
        IReadOnlyList<ConsoleContentAction> candidateActions,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Evaluates route-level entitlements. The result is keyed by route key.
    /// </summary>
    Task<IReadOnlyList<ConsoleNavigationEntitlement>> EvaluateRouteEntitlementsAsync(
        ClaimsPrincipal principal,
        IReadOnlyList<string> routeKeys,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the capability string set for a principal — used by the
    /// bootstrap endpoint to populate <see cref="ConsoleSessionContext.Capabilities"/>.
    /// </summary>
    Task<IReadOnlyList<string>> ResolveCapabilitiesAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default);
}

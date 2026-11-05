// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Console.Domain;

namespace Honua.Core.Features.Console.Abstractions;

public interface IConsoleActionEvaluator
{
    Task<IReadOnlyList<ConsoleContentAction>> EvaluateItemActionsAsync(
        ClaimsPrincipal principal,
        ConsoleContentItem item,
        IReadOnlyList<ConsoleContentAction> candidateActions,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, IReadOnlyList<ConsoleContentAction>>> EvaluateItemActionsAsync(
        ClaimsPrincipal principal,
        IReadOnlyList<ConsoleContentItem> items,
        IReadOnlyList<ConsoleContentAction> candidateActions,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConsoleNavigationEntitlement>> EvaluateRouteEntitlementsAsync(
        ClaimsPrincipal principal,
        IReadOnlyList<string> routeKeys,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ResolveCapabilitiesAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default);
}

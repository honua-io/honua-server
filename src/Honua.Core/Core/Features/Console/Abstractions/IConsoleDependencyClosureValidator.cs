// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Console.Domain;

namespace Honua.Core.Features.Console.Abstractions;

public interface IConsoleDependencyClosureValidator
{
    Task<IReadOnlyList<ConsoleShareDependencyConflict>> ValidateAsync(
        string itemId,
        ConsoleShareAccessTier targetTier,
        int maxDepth,
        CancellationToken cancellationToken = default);
}

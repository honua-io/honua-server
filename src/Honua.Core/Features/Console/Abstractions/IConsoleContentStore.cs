// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Console.Domain;

namespace Honua.Core.Features.Console.Abstractions;

public interface IConsoleContentStore
{
    Task<ConsoleContentListResult> ListAsync(ConsoleContentQuery query, CancellationToken cancellationToken = default);

    Task<ConsoleContentItem?> GetAsync(string id, CancellationToken cancellationToken = default);

    Task<ConsoleContentItem> CreateAsync(ConsoleContentItem item, CancellationToken cancellationToken = default);

    // A supplied generation must match the stored one exactly; a null generation is last-write-wins.
    Task<ConsoleContentItem?> UpdateAsync(ConsoleContentItem item, CancellationToken cancellationToken = default);

    // A patch that would leave team visibility without a team scope is rejected inside the write, because the patch itself cannot set the scope.
    Task<ConsoleContentItem?> PatchAsync(string id, ConsoleContentItemPatch patch, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConsoleContentItem>> GetProvenanceChainAsync(string id, int maxDepth, CancellationToken cancellationToken = default);
}

public sealed record ConsoleContentItemPatch
{
    public string? Title { get; init; }

    public string? Description { get; init; }

    public IReadOnlyList<string>? Tags { get; init; }

    public IReadOnlyDictionary<string, string>? Labels { get; init; }

    public ConsoleVisibility? Visibility { get; init; }

    public string? UpdatedById { get; init; }
}

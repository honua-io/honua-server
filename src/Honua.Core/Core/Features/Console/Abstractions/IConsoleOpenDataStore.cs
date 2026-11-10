// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Console.Domain;

namespace Honua.Core.Features.Console.Abstractions;

public interface IConsoleOpenDataStore
{
    Task<ConsoleOpenDataPage?> GetPageAsync(string itemId, CancellationToken cancellationToken = default);

    Task<ConsoleOpenDataPage> SavePageAsync(ConsoleOpenDataPage page, string? principalId, CancellationToken cancellationToken = default);

    Task<ConsoleStacPublicationState> GetStacPublicationAsync(string itemId, CancellationToken cancellationToken = default);

    Task<ConsoleStacPublicationState> PublishStacAsync(string itemId, string? principalId, CancellationToken cancellationToken = default);

    Task<ConsoleStacPublicationState?> UnpublishStacAsync(string itemId, string? principalId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListPublishedStacItemIdsAsync(CancellationToken cancellationToken = default);
}

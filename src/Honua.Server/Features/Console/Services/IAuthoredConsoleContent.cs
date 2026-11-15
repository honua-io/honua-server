// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Console.Abstractions;
using Honua.Core.Features.Console.Domain;

namespace Honua.Server.Features.Console.Services;

// The items people created in the Console, as opposed to the items projected from published metadata.
internal interface IAuthoredConsoleContent : IConsoleContentStore
{
    ValueTask<IReadOnlyCollection<ConsoleContentItem>> SnapshotItemsAsync(CancellationToken cancellationToken);
}

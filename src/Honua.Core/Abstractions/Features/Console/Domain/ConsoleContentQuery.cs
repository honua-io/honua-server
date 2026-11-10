// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Console.Domain;

public sealed record ConsoleContentQuery
{
    public string? SearchTerm { get; init; }

    public IReadOnlyList<ConsoleContentItemType>? ItemTypes { get; init; }

    public IReadOnlyList<ConsoleVisibility>? Visibilities { get; init; }

    public string? OwnerId { get; init; }

    public string? Namespace { get; init; }

    public string? Cursor { get; init; }

    public int Limit { get; init; } = 25;
}

public sealed record ConsoleContentListResult
{
    public IReadOnlyList<ConsoleContentItem> Items { get; init; } = Array.Empty<ConsoleContentItem>();

    public int Total { get; init; }

    public string? NextCursor { get; init; }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Studio.Drafts;

public sealed record PackageDraftFinding
{
    public required string Code { get; init; }

    public required string Path { get; init; }

    public required string Message { get; init; }

    public string Describe() => $"{Code} {Path}: {Message}";
}

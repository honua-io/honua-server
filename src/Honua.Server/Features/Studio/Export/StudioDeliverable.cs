// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Studio.Domain;

namespace Honua.Server.Features.Studio.Export;

public enum StudioDeliverableFormat
{
    Png,

    Pdf,
}

public sealed record StudioDeliverableArtifact
{
    public required byte[] Content { get; init; }

    public required string ContentType { get; init; }

    public required string FileName { get; init; }

    public required StudioPackageFamily Family { get; init; }

    public required StudioDeliverableFormat Format { get; init; }
}

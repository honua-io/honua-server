// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Admin.Domain;

public enum LayerPublishAdmissionKind
{
    Completed,
    Queued,
    Unavailable,
}

public sealed record LayerPublishAdmission(
    LayerPublishAdmissionKind Kind,
    PublishedLayerSummary? Layer,
    string? JobId,
    long RowsTotal,
    string? StatusUrl);

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Domain;

public sealed record WorkspaceArtifactPublication(
    string WorkspaceId, string OperationId, int OutputSlot, ArtifactKind Kind,
    string Label, bool Overwrite, string Reference);

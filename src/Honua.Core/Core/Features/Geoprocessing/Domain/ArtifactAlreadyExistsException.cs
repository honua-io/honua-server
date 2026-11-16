// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Domain;

public sealed class ArtifactAlreadyExistsException : Exception
{
    public string WorkspaceId { get; }

    public string Label { get; }

    public ArtifactAlreadyExistsException(string workspaceId, string label)
        : base(
            $"Output '{label}' already exists in workspace '{workspaceId}'. " +
            "Set env:overwriteOutput=true to replace it.")
    {
        WorkspaceId = workspaceId;
        Label = label;
    }
}

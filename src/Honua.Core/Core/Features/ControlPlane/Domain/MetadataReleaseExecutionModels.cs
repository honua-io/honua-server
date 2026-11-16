// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.ControlPlane.Domain;

public enum MetadataReleaseScriptOperationKind
{
    AddColumn,

    DropColumn
}

public sealed record MetadataReleaseScriptOperation
{
    public required MetadataReleaseScriptOperationKind Kind { get; init; }

    public required string ResourceSemanticId { get; init; }

    public required string FieldName { get; init; }

    public string? FieldType { get; init; }

    public bool Nullable { get; init; } = true;
}

public sealed record MetadataReleaseScript
{
    public required string ScriptId { get; init; }

    public bool Reversible { get; init; } = true;

    public required IReadOnlyList<MetadataReleaseScriptOperation> ForwardOperations { get; init; }

    public IReadOnlyList<MetadataReleaseScriptOperation> InverseOperations { get; init; }
        = Array.Empty<MetadataReleaseScriptOperation>();

    public IReadOnlyList<MetadataReleaseScriptOperation> ResolveInverseOperations()
    {
        if (InverseOperations.Count > 0)
        {
            return InverseOperations;
        }

        var derived = new List<MetadataReleaseScriptOperation>(ForwardOperations.Count);
        for (var index = ForwardOperations.Count - 1; index >= 0; index--)
        {
            var forward = ForwardOperations[index];
            if (forward.Kind == MetadataReleaseScriptOperationKind.AddColumn)
            {
                derived.Add(new MetadataReleaseScriptOperation
                {
                    Kind = MetadataReleaseScriptOperationKind.DropColumn,
                    ResourceSemanticId = forward.ResourceSemanticId,
                    FieldName = forward.FieldName
                });
            }
        }

        return derived;
    }
}

public sealed record MetadataReleaseExecutionPlan
{
    public required string PackageId { get; init; }

    public required string TargetEnvironment { get; init; }

    public required string ResourceSemanticId { get; init; }

    public required string NewFieldName { get; init; }

    public required MetadataReleaseScript Script { get; init; }

    public string? DataPopulateWorkloadId { get; init; }

    public IReadOnlyList<string> DataPopulateFields { get; init; } = Array.Empty<string>();
}

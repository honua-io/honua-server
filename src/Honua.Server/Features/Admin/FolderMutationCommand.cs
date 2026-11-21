// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Operations.Domain;

namespace Honua.Server.Features.Admin;

// Closed commands keep the operation identity and its mutation together across transports.
// Tenant selection and authorization are evaluated by the application boundary, not the payload.
internal abstract record FolderMutationCommand
{
    private FolderMutationCommand() { }

    internal abstract string OperationId { get; }
    internal abstract FolderMutation Apply(MetadataV2Graph graph, string? tenant);

    internal static FolderMutationCommand FromRequest(OperationRequest request)
    {
        var parameters = request.Parameters;
        var name = parameters.GetValueOrDefault("name");
        var title = parameters.GetValueOrDefault("title");
        return request.OperationId switch
        {
            "folder.create" => new Create(name, parameters.ContainsKey("title") ? new FolderTitleChange(title) : default),
            "folder.update" => new Update(parameters["id"]!, name, parameters.ContainsKey("title") ? new FolderTitleChange(title) : default),
            "folder.delete" => new Delete(parameters["id"]!),
            "service.move" => new Move(request.ServiceName!, parameters.TryGetValue("folderId", out var id)
                ? new MetadataV2FolderSelection(id) : default),
            _ => throw new FolderProposalRefusal(FolderApprovalAuthorization.FreshProposalRequired)
        };
    }

    internal sealed record Restore(string ServiceName, IReadOnlyList<ServiceFolderAssignment> Assignments) : FolderMutationCommand
    {
        internal override string OperationId => "service.move";
        internal override FolderMutation Apply(MetadataV2Graph graph, string? tenant)
            => FolderMutation.Restore(graph, this, tenant);
    }

    internal sealed record Create(string? Name, FolderTitleChange Title) : FolderMutationCommand
    {
        internal override string OperationId => "folder.create";
        internal override FolderMutation Apply(MetadataV2Graph graph, string? tenant)
            => FolderMutation.Create(graph, this, tenant);
    }

    internal sealed record Update(string Id, string? Name, FolderTitleChange Title) : FolderMutationCommand
    {
        internal override string OperationId => "folder.update";
        internal override FolderMutation Apply(MetadataV2Graph graph, string? tenant)
            => FolderMutation.Update(graph, this, tenant);
    }

    internal sealed record Delete(string Id) : FolderMutationCommand
    {
        internal override string OperationId => "folder.delete";
        internal override FolderMutation Apply(MetadataV2Graph graph, string? tenant)
            => FolderMutation.Delete(graph, Id, tenant);
    }

    internal sealed record Move(string ServiceName, MetadataV2FolderSelection FolderId) : FolderMutationCommand
    {
        internal override string OperationId => "service.move";
        internal override FolderMutation Apply(MetadataV2Graph graph, string? tenant)
            => FolderMutation.Move(graph, this, tenant);
    }
}

internal readonly record struct FolderTitleChange
{
    internal FolderTitleChange(string? value)
    {
        Value = value;
        IsSpecified = true;
    }

    internal string? Value { get; }
    internal bool IsSpecified { get; }
}

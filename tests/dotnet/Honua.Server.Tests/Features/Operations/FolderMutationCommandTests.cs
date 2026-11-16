// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Operations.Domain;
using Honua.Server.Features.Admin;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.OperationsToolset;

public sealed class FolderMutationCommandTests
{
    [UnitTheory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(true, "Survey title")]
    public void Create_SealedAndHttpInputs_PreserveExplicitTitlePresence(bool specified, string? title)
    {
        var parameters = new Dictionary<string, string?> { ["name"] = "survey" };
        var body = new FolderRequest { Name = "survey" };
        if (specified)
        {
            parameters["title"] = title;
            body.Title = title;
        }
        var sealedCommand = FolderMutationCommand.FromRequest(new OperationRequest
        { OperationId = "folder.create", Parameters = parameters });
        var httpCommand = new FolderMutationCommand.Create(body.Name,
            body.TitleSpecified ? new FolderTitleChange(body.Title) : default);

        sealedCommand.Should().Be(httpCommand);
        sealedCommand.Should().NotBe(new FolderMutationCommand.Create(body.Name,
            specified ? default : new FolderTitleChange(null)));
        sealedCommand.Apply(new MetadataV2Graph(), null).Folder!.Metadata.Title.Should().Be(title);
    }

    [UnitTheory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(true, "New title")]
    public void Update_SealedAndHttpInputs_PreserveTitleSelection(bool specified, string? title)
    {
        var graph = new MetadataV2Graph
        {
            Folders = [new MetadataV2Folder { Metadata = new MetadataV2ObjectMetadata
                { Id = "folder", Name = "survey", Title = "Existing title" } }]
        };
        var parameters = new Dictionary<string, string?> { ["id"] = "folder" };
        var body = new FolderRequest();
        if (specified)
        {
            parameters["title"] = title;
            body.Title = title;
        }
        var sealedCommand = FolderMutationCommand.FromRequest(new OperationRequest
        { OperationId = "folder.update", Parameters = parameters });
        var httpCommand = new FolderMutationCommand.Update("folder", body.Name,
            body.TitleSpecified ? new FolderTitleChange(body.Title) : default);

        sealedCommand.Should().Be(httpCommand);
        foreach (var command in new[] { sealedCommand, httpCommand })
            command.Apply(graph, null).Folder!.Metadata.Title.Should().Be(specified ? title : "Existing title");
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Move_SealedInput_RequiresExplicitFolderSelection(bool specified)
    {
        var graph = new MetadataV2Graph
        {
            Services = [new MetadataV2Service { Metadata = new MetadataV2ObjectMetadata { Id = "service", Name = "survey" } }]
        };
        var parameters = new Dictionary<string, string?>();
        if (specified) parameters["folderId"] = null;
        var command = FolderMutationCommand.FromRequest(new OperationRequest
        { OperationId = "service.move", ServiceName = "survey", Parameters = parameters });

        var apply = () => command.Apply(graph, null);

        if (specified) apply.Should().NotThrow();
        else apply.Should().Throw<ArgumentException>().WithMessage("folderId is required; null selects root.");
    }
}

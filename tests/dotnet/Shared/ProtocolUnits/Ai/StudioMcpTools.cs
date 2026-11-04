// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Ai.Protocols.Mcp.Studio;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Geoprocessing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

internal static class StudioMcpTools
{
    internal static IMcpTool[] Create(IGeoprocessingJobService jobService) =>
    [
        new CreateStudioDraftTool(jobService, NullLogger<CreateStudioDraftTool>.Instance),
        new GetStudioDraftTool(jobService, NullLogger<GetStudioDraftTool>.Instance),
        new UpdateStudioDraftTool(jobService, NullLogger<UpdateStudioDraftTool>.Instance),
        new ValidateStudioDraftTool(jobService, NullLogger<ValidateStudioDraftTool>.Instance),
        new PreviewStudioDraftTool(jobService, NullLogger<PreviewStudioDraftTool>.Instance),
        new SaveStudioVersionTool(jobService, NullLogger<SaveStudioVersionTool>.Instance),
        new GetStudioVersionTool(jobService, NullLogger<GetStudioVersionTool>.Instance),
        new ReopenStudioVersionTool(jobService, NullLogger<ReopenStudioVersionTool>.Instance),
        new AddStudioLayerTool(jobService, NullLogger<AddStudioLayerTool>.Instance),
        new RemoveStudioLayerTool(jobService, NullLogger<RemoveStudioLayerTool>.Instance),
        new SetStudioLayerStyleTool(jobService, NullLogger<SetStudioLayerStyleTool>.Instance),
        new SetStudioLayerVisibilityTool(jobService, NullLogger<SetStudioLayerVisibilityTool>.Instance),
        new SetStudioMapStyleTool(jobService, NullLogger<SetStudioMapStyleTool>.Instance),
        new SetStudioViewTool(jobService, NullLogger<SetStudioViewTool>.Instance),
        new AddStudioWidgetTool(jobService, NullLogger<AddStudioWidgetTool>.Instance),
        new RemoveStudioWidgetTool(jobService, NullLogger<RemoveStudioWidgetTool>.Instance),
        new BindStudioInteractionTool(jobService, NullLogger<BindStudioInteractionTool>.Instance),
        new RemoveStudioInteractionTool(jobService, NullLogger<RemoveStudioInteractionTool>.Instance),
        new AddStudioControlTool(jobService, NullLogger<AddStudioControlTool>.Instance),
        new RemoveStudioControlTool(jobService, NullLogger<RemoveStudioControlTool>.Instance),
        new ProposeStudioPublicationTool(jobService, NullLogger<ProposeStudioPublicationTool>.Instance),
    ];
}

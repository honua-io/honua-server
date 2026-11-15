// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Resources;
using Honua.Geoprocessing;

namespace Honua.Ai.Protocols.Mcp;

internal sealed partial class McpDataAccessSurface
{
    private McpJsonRpcResponse ListSkills(McpJsonRpcRequest request)
    {
        if (!TryReadCursor(request, out var cursor, out var error)) return error;
        try
        {
            var page = McpPagination.Page(OperatingSkillsResource.Instance.Skills, cursor, _limits.ListPageSize, out var nextCursor);
            return SuccessResponse(request.Id, new McpSkillsListResult { Skills = page, NextCursor = nextCursor },
                McpJsonContext.Default.McpSkillsListResult);
        }
        catch (GeoprocessingValidationException ex)
        {
            return ErrorResponse(request.Id, McpErrorMapper.InvalidArgument(ex.Message));
        }
    }

    private static McpJsonRpcResponse GetSkill(McpJsonRpcRequest request)
    {
        try
        {
            var parameters = ParseParams(request.Params, McpJsonContext.Default.McpResourcesReadParams);
            var skill = OperatingSkillsResource.Instance.Skills.FirstOrDefault(
                skill => string.Equals(skill.Uri, parameters?.Uri, StringComparison.Ordinal));
            return skill is null
                ? ErrorResponse(request.Id, McpErrorMapper.InvalidArgument("Unknown skill URI."))
                : SuccessResponse(request.Id, new McpSkillsGetResult(skill), McpJsonContext.Default.McpSkillsGetResult);
        }
        catch (GeoprocessingValidationException ex)
        {
            return ErrorResponse(request.Id, McpErrorMapper.InvalidArgument(ex.Message));
        }
    }
}

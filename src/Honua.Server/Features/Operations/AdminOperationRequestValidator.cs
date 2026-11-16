// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Operations.Domain;

namespace Honua.Server.Features.Operations;

internal static class AdminOperationRequestValidator
{
    internal static OperationValidation Validate(AdminApiOperationCatalog.Definition definition, OperationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var descriptor = AdminApiOperationCatalog.Descriptors.Single(item => item.OperationId == definition.OperationId);
        var routeNames = RouteNames(definition.Path);
        var missing = routeNames.Where(name => string.IsNullOrWhiteSpace(name switch
        {
            "connectionId" => request.ConnectionId,
            "serviceName" => request.ServiceName,
            _ => request.Parameters.GetValueOrDefault(name)
        })).Select(name => $"Required route parameter '{name}' is missing.").ToList();
        foreach (var parameter in descriptor.InputSchema.Where(static parameter => parameter.Required))
        {
            var present = parameter.Name switch
            {
                "connectionId" => !string.IsNullOrWhiteSpace(request.ConnectionId),
                "serviceName" when routeNames.Contains("serviceName") => !string.IsNullOrWhiteSpace(request.ServiceName),
                "body" or "folderId" => request.Parameters.ContainsKey(parameter.Name),
                _ => request.Parameters.TryGetValue(parameter.Name, out var value) && !string.IsNullOrWhiteSpace(value)
            };
            if (!present) missing.Add($"Required parameter '{parameter.Name}' is missing.");
        }
        return new OperationValidation { IsValid = missing.Count == 0, Status = missing.Count == 0 ? "valid" : "invalid", Messages = missing.ToArray() };
    }

    private static HashSet<string> RouteNames(string path) => path.Split('/').Where(static segment => segment.StartsWith('{') && segment.EndsWith('}')).Select(static segment => segment[1..^1]).ToHashSet(StringComparer.Ordinal);
}

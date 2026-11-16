// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Operations;

internal static class AdminOperationEndpointExtensions
{
    public static RouteHandlerBuilder WithAdminOperation(
        this RouteHandlerBuilder endpoint, AdminApiOperationCatalog.Definition definition)
        => endpoint.WithName(definition.OpenApiOperationId).WithMetadata(definition);
}

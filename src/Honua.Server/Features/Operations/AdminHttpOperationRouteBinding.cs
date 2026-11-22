// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Operations.Abstractions;
using Honua.Infrastructure.Hosting;
using Microsoft.AspNetCore.Routing;

namespace Honua.Server.Features.Operations;

internal static class AdminHttpOperationRouteBinding
{
    public static AdminOperationRouteBinding ForApi(
        string operationId,
        string groupPrefix,
        string? relativeTemplate = null) =>
        AdminHttpOperationBinding.ForDefinition(
            AdminApiOperationCatalog.Definitions.Single(item => item.OperationId == operationId),
            groupPrefix,
            relativeTemplate);

    public static AdminOperationRouteBinding ForOperate(
        string operationId,
        string groupPrefix,
        string? relativeTemplate = null) =>
        AdminHttpOperationBinding.ForDefinition(
            AdminOperateOperationCatalog.Definitions.Single(item => item.OperationId == operationId),
            groupPrefix,
            relativeTemplate);

    public static AdminOperationRouteBinding ForAccess(
        string operationId,
        string groupPrefix,
        string? relativeTemplate = null) =>
        AdminHttpOperationBinding.ForDefinition(
            AdminAccessOperationCatalog.Definitions.Single(item => item.OperationId == operationId),
            groupPrefix,
            relativeTemplate);

    public static AdminOperationRouteBinding ForConnectImport(
        string operationId,
        string groupPrefix,
        string? relativeTemplate = null) =>
        AdminHttpOperationBinding.ForDefinition(
            AdminConnectImportOperationCatalog.Definitions.Single(item => item.OperationId == operationId),
            groupPrefix,
            relativeTemplate);

    public static IReadOnlyList<AdminHttpOperationBindingViolation> Validate(
        IEnumerable<Endpoint> endpoints,
        IEnumerable<IAdminHttpOperationDefinition> definitions) =>
        AdminHttpOperationBinding.Validate(endpoints, definitions);
}

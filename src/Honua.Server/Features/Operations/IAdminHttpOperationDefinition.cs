// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Operations.Domain;

namespace Honua.Server.Features.Operations;

internal interface IAdminHttpOperationDefinition
{
    string OperationId { get; }
    string Title { get; }
    HttpMethod Method { get; }
    string Path { get; }
    string OpenApiOperationId { get; }
    OperationSideEffectClass SideEffect { get; }
    OperationBlastRadiusClass BlastRadius { get; }
    bool SupportsDryRun { get; }
    string? DryRunPath { get; }
    HttpMethod? DryRunMethod { get; }
    string? ContentType { get; }
    OperationApprovalModel? ApprovalModel { get; }
    OperationClass OperationClass { get; }
}

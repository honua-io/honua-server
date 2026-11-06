// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Guardrails.Domain;

public enum OperationClass
{
    AdminConfigChange = 0,

    Deploy = 1,

    MetadataRelease = 2,

    Seed = 3,

    Geoprocess = 4,

    // An approval replay of this class cannot be routed to an unrelated actuator.
    ServicePublish = 5,

    StudioDraftMutation = 6,
}

public enum GuardrailTier
{
    DirectExecute = 0,

    RequiresApproval = 1,

    Blocked = 2,
}

public sealed record GuardrailDecision(
    GuardrailTier Tier,
    OperationClass OperationClass,
    Honua.Core.Features.Licensing.Domain.HonuaEdition Edition,
    string Source);

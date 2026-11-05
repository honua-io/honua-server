// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Licensing.Domain;

namespace Honua.Core.Features.Guardrails.Abstractions;

public interface IGuardrailLadder
{
    GuardrailDecision Resolve(OperationClass operationClass, HonuaEdition edition);

    GuardrailDecision Resolve(OperationClass operationClass);

    GuardrailDecision Resolve(OperationClass operationClass, string? actionDiscriminator);

    GuardrailDecision Resolve(OperationClass operationClass, string? actionDiscriminator, HonuaEdition edition);
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Guardrails.Domain;

namespace Honua.Core.Features.Guardrails.Abstractions;

public interface IOpsActionGuardrailCatalog
{
    bool TryGetTier(string action, out GuardrailTier tier);
}

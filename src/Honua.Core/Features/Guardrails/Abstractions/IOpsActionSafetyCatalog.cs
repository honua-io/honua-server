// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Guardrails.Domain;

namespace Honua.Core.Features.Guardrails.Abstractions;

/// <summary>
/// Supplies route-time safety metadata for control-plane ops actions.
/// </summary>
public interface IOpsActionSafetyCatalog
{
    /// <summary>
    /// Returns true only when the operation/action pair is registered as safe for autonomous apply.
    /// </summary>
    /// <param name="operationClass">Operation class routed through the gateway.</param>
    /// <param name="actionDiscriminator">Action discriminator, when applicable.</param>
    /// <returns>True when the action is auto-safe; otherwise false.</returns>
    bool IsAutoSafe(OperationClass operationClass, string? actionDiscriminator);
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Guardrails.Domain;

namespace Honua.Core.Features.Guardrails;

public sealed class GuardrailLadderOptions
{
    public const string SectionName = "Guardrails";

    public bool FailClosed { get; set; }

    public IDictionary<string, string> Overrides { get; set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

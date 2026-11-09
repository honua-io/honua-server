// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Guardrails.Domain;

public static class BuiltInGuardrailActions
{
    public const string StudioPublicationProposal = "studio.publication_proposal";

    // This publication keeps the edition tier and never takes the Licensing:Mode=Disabled direct tier that Studio draft composition receives.
    public const string StudioPublicationRequest = "studio.publication_request";

    public const string StudioRollback = "studio.rollback";

    public static bool TryGetTier(string action, out GuardrailTier tier)
    {
        if (string.Equals(action, StudioPublicationProposal, StringComparison.Ordinal))
        {
            tier = GuardrailTier.RequiresApproval;
            return true;
        }

        if (string.Equals(action, StudioPublicationRequest, StringComparison.Ordinal) ||
            string.Equals(action, StudioRollback, StringComparison.Ordinal))
        {
            tier = GuardrailTier.DirectExecute;
            return true;
        }

        tier = GuardrailTier.Blocked;
        return false;
    }
}

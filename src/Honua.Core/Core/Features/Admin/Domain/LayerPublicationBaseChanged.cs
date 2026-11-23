// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Admin.Domain;

// The conflict a pinned publication raises when the catalog moved past its write base.
// Approved replays report it as a freshness failure, not as an actuation fault.
public static class LayerPublicationBaseChanged
{
    public const string Message =
        "Publication metadata changed after preview. Create a new preview before publishing.";

    public const string ProposalReason =
        "The catalog changed after this publish proposal was submitted. Submit a fresh proposal.";

    public static LayerPublishingException Create() => new(LayerPublishingErrorKind.Conflict, Message);

    public static bool Matches(LayerPublishingException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.ErrorKind == LayerPublishingErrorKind.Conflict &&
            string.Equals(exception.Message, Message, StringComparison.Ordinal);
    }
}

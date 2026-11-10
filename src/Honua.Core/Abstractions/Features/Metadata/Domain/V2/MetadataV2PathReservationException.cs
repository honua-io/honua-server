// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Domain.V2;

public sealed class MetadataV2PathReservationException() : InvalidOperationException(RecoveryMessage)
{
    public const string RecoveryMessage = "Activation would erase or rebind permanent public path reservations. Use the metadata release revert path to create a reconciled new revision.";
}

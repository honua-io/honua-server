// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Collaboration.Sessions;

// Callers must not infer distributed ordering or retained presence from successful pub/sub, and the default registration is a no-op.
internal interface ICollaborationSessionBackplane
{
    // Publish must not block the caller and must swallow transport failures.
    void Publish(CollaborationEventEnvelope ev);

    // Capabilities come from actual delivery, not the declared topology, so a no-op backplane cannot advertise cursors.
    bool SupportsCrossReplicaDelivery { get; }

    bool SupportsOrderedOperationDelivery => false;

    bool SupportsReplicaWidePresence => false;
}

internal sealed class NullCollaborationSessionBackplane : ICollaborationSessionBackplane
{
    public static NullCollaborationSessionBackplane Instance { get; } = new();

    private NullCollaborationSessionBackplane()
    {
    }

    public bool SupportsCrossReplicaDelivery => false;

    public void Publish(CollaborationEventEnvelope ev)
    {
        // Intentionally no-op: single-node delivery is handled by the in-memory outboxes.
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Tests.Features.Assets;

public sealed partial class WorkTaskCompletionTests
{
    public enum ConfigurationWriteTiming { Committed, Concurrent }
    public enum UploadExpiry { Future, Past }
    public enum ReplayPayload { Same, Changed }
    public enum ReservationOutcome { Rejected, Accepted }
    public enum TaskChange { Reassigned, Cancelled }
}

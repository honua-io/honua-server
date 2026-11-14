// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Observability.Domain;

public sealed record OpsExternalEvent(string Id, DateTimeOffset OccurredAt, string Body);

public sealed record OpsEventQueueMessage(string ReceiptHandle, string Body);

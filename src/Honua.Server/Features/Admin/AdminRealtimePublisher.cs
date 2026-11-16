// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Realtime;

namespace Honua.Server.Features.Admin;

internal sealed class AdminRealtimePublisher(IServiceProvider services)
{
    public async Task PublishAsync(AdminRealtimeMessage message, CancellationToken cancellationToken)
    {
        await services.GetRequiredService<IRealtimeFanoutProvider>().AppendAsync(new RealtimeEventDraft(RealtimeFamily.Admin,
            message.Topic, message.Kind, message.Payload), null, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed record AdminRealtimeMessage(string Topic, string Kind, JsonElement Payload);

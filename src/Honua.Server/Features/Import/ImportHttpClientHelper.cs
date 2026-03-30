// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http;
using Honua.Server.Features.Infrastructure.Events;

namespace Honua.Server.Features.Import;

/// <summary>
/// Shared HTTP client wiring for import-related outbound requests.
/// </summary>
internal static class ImportHttpClientHelper
{
    internal static HttpMessageHandler CreatePinnedDnsHttpMessageHandler()
        => WebhookDeliveryHelper.CreatePinnedDnsHttpMessageHandler();
}

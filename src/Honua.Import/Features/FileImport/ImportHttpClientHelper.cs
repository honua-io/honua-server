// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http;
using Honua.Server.Features.Infrastructure.Events;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Import;
using Honua.Server.Features.Migration;
using Honua.Import.FileImport;
using Honua.Import.RasterImport;

namespace Honua.Import.FileImport;

/// <summary>
/// Shared HTTP client wiring for import-related outbound requests.
/// </summary>
internal static class ImportHttpClientHelper
{
    internal static HttpMessageHandler CreatePinnedDnsHttpMessageHandler()
        => WebhookDeliveryHelper.CreatePinnedDnsHttpMessageHandler();
}

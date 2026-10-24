// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using Azure;
using Honua.Core.Features.Infrastructure.Abstractions;

namespace Honua.FileStorage;

/// <summary>
/// Recognizes Azure Blob "missing object" failures (<see cref="RequestFailedException"/>
/// with a 404 status) so cloud-neutral orchestrators (e.g. the PMTiles range
/// proxy) can surface them as 404 rather than 500. Registered as one of the
/// <see cref="Honua.Core.Features.Infrastructure.Abstractions.ICloudNotFoundClassifier"/>
/// implementations consumed by <c>PMTilesProxyService</c>.
/// </summary>
internal sealed class AzureBlobNotFoundClassifier : Honua.Core.Features.Infrastructure.Abstractions.ICloudNotFoundClassifier
{
    public bool IsNotFound(Exception exception) =>
        exception is RequestFailedException azure &&
        azure.Status == (int)HttpStatusCode.NotFound;
}

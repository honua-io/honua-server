// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using Azure;
using Honua.Core.Features.Infrastructure.Abstractions;

namespace Honua.FileStorage;

internal sealed class AzureBlobNotFoundClassifier : Honua.Core.Features.Infrastructure.Abstractions.ICloudNotFoundClassifier
{
    public bool IsNotFound(Exception exception) =>
        exception is RequestFailedException azure &&
        azure.Status == (int)HttpStatusCode.NotFound;
}

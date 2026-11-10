// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Infrastructure.Domain;

[JsonConverter(typeof(JsonStringEnumConverter<CloudStorageProvider>))]
public enum CloudStorageProvider
{
    Local = 0,

    AwsS3 = 1,

    AzureBlob = 2
}

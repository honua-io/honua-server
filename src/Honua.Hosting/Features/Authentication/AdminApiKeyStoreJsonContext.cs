// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Infrastructure.Authentication;

// Keep default names, nulls and property order: validation compares the serialized
// snapshot with existing Redis bytes before updating LastUsedAt.
[JsonSerializable(typeof(AdminApiKeyRecord))]
internal partial class AdminApiKeyStoreJsonContext : JsonSerializerContext
{
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.EmbedGovernance.Domain;

namespace Honua.Core.Features.EmbedGovernance.Abstractions;

public sealed record EmbedKeyCreateResult(EmbedKeyRecord Record, string Key);

public sealed record EmbedKeyValidationResult(EmbedKeyRecord Record);

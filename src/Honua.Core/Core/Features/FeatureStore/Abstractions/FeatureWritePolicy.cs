// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Queries.Filters;

namespace Honua.Core.Features.FeatureStore.Abstractions;

internal sealed record FeatureWritePolicy(int LayerId, SqlFragment? RowFilter);

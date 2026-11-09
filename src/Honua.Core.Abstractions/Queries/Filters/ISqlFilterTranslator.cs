// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Queries.Filters;

public interface ISqlFilterTranslator
{
    SqlFragment Translate(FilterExpression filter, MetadataV2Object resource);
}

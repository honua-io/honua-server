// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Federation.Domain;

public enum FederatedSourceKind
{
    HonuaGrpc,

    EsriRest,

    // An OGC source pushes down CQL2 attribute and bbox predicates; richer spatial relationships and ordering stay local.
    OgcWfs
}

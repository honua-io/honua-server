// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.WorkflowPackages.Domain;

public enum WorkflowNodeRuntimeKind
{
    Geoprocessing,

    ExtractTransformLoad,

    WorkflowUtility,

    Authoring
}

public enum WorkflowSchemaValueType
{
    Text,

    WholeNumber,

    DecimalNumber,

    Flag,

    List,

    Structured
}

public enum WorkflowEdgeKind
{
    Data,

    Control,

    Failure
}

public enum WorkflowPublicationTarget
{
    Job,

    Schedule,

    ProcessEndpoint
}

public enum WorkflowPublicationStatus
{
    Active,

    Disabled
}

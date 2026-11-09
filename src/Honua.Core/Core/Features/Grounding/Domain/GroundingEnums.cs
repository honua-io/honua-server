// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Grounding.Domain;

public enum WorkflowFamily
{
    Analyze,

    PublishData,

    BuildApp,

    AutomateDeploy
}

public enum ConfidenceBand
{
    Low,

    Medium,

    High
}

public enum CandidateKind
{
    Dataset,

    Process,

    Template,

    Style,

    Deployment
}

public enum DatasetSubtype
{
    Layer,

    Service
}

public enum GroundingErrorKind
{
    EmptyGoal,

    UnsupportedWorkflowFamily,

    CatalogUnavailable,

    UnknownIntent
}

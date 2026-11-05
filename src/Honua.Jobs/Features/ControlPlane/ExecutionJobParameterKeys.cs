// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.ControlPlane;

internal static class ExecutionJobParameterKeys
{
    public const string MetadataListSeparator = "|";

    public const string DefinitionId = "honua.job.definition_id";

    public const string Queue = "honua.job.queue";

    public const string ResourceRefs = "honua.job.resource_refs";

    public const string ParentId = "honua.job.parent_id";

    public const string ChildIds = "honua.job.child_ids";

    public const string Environment = "honua.job.environment";

    public const string Server = "honua.job.server";

    public const string ReleaseId = "honua.job.release_id";

    public const string ChangeSetId = "honua.job.change_set_id";

    public const string AlertId = "honua.job.alert_id";

    public const string TraceId = "honua.trace_id";

    public const string GeoprocessingPlanId = "honua.geoprocessing.plan_id";

    public const string GeoprocessingProcessDefinitions = "honua.geoprocessing.process_definitions";

    public const string GeoprocessingOutputArtifactKinds = "honua.geoprocessing.output_artifact_kinds";

    public const string ShareExportId = "honua.share.export_id";

    public const string ShareRunId = "honua.share.run_id";

    public const string ShareDestinationType = "honua.share.destination_type";

    public const string ShareFormat = "honua.share.format";

    public const string GeoprocessingStepInputPrefix = "honua.geoprocessing.step.";

    public const string GeoprocessingStepRasterSourcePrefix = "honua.geoprocessing.raster_source.";

    public const string GeoprocessingOutputRegistrationPrefix = "honua.geoprocessing.output_registration.";
}

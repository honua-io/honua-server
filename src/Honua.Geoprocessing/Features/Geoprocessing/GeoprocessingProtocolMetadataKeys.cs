// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Geoprocessing;

internal static class GeoprocessingProtocolMetadataKeys
{
    public const string GPServerServiceId = "gpserver.serviceId";

    public const string GPServerTaskName = "gpserver.taskName";

    public const string GPServerContext = "gpserver.context";

    public const string GPServerOutSr = "gpserver.env.outSR";

    // New requests reject a processing spatial reference, and this key remains only so existing job metadata can still be read.
    public const string GPServerProcessSr = "gpserver.env.processSR";

    // The working SRID is stored so a completed job is reprojected the same way the synchronous path does, instead of accepting the output SR and ignoring it.
    public const string GPServerWorkingSr = "gpserver.env.workingSR";

    public const string GPServerFeatureSchema = "gpserver.featureSchema";

    public const string GPServerWorkspace = "gpserver.env.workspace";

    public const string GPServerOverwriteOutput = "gpserver.env.overwriteOutput";

    public const string OutputNamePrefix = "process.output.";

    public const string GPServerOutputNamePrefix = "gpserver.output.";

    public const string GeoServicesOutputParameterMetadataKey = "geoservices.output_parameter";
}

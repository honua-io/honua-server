// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Shared.Models;

// The built-in 4326 and 3857 answers include WKT so exporters are not given an empty definition for those SRIDs.
public static class WellKnownCrsWkt
{
    public const string Epsg4326 =
        "GEOGCS[\"WGS 84\",DATUM[\"WGS_1984\",SPHEROID[\"WGS 84\",6378137,298.257223563," +
        "AUTHORITY[\"EPSG\",\"7030\"]],AUTHORITY[\"EPSG\",\"6326\"]]," +
        "PRIMEM[\"Greenwich\",0,AUTHORITY[\"EPSG\",\"8901\"]]," +
        "UNIT[\"degree\",0.0174532925199433,AUTHORITY[\"EPSG\",\"9122\"]]," +
        "AUTHORITY[\"EPSG\",\"4326\"]]";

    public const string Epsg3857 =
        "PROJCS[\"WGS 84 / Pseudo-Mercator\",GEOGCS[\"WGS 84\",DATUM[\"WGS_1984\"," +
        "SPHEROID[\"WGS 84\",6378137,298.257223563,AUTHORITY[\"EPSG\",\"7030\"]]," +
        "AUTHORITY[\"EPSG\",\"6326\"]],PRIMEM[\"Greenwich\",0,AUTHORITY[\"EPSG\",\"8901\"]]," +
        "UNIT[\"degree\",0.0174532925199433,AUTHORITY[\"EPSG\",\"9122\"]]," +
        "AUTHORITY[\"EPSG\",\"4326\"]],PROJECTION[\"Mercator_1SP\"]," +
        "PARAMETER[\"central_meridian\",0],PARAMETER[\"scale_factor\",1]," +
        "PARAMETER[\"false_easting\",0],PARAMETER[\"false_northing\",0]," +
        "UNIT[\"metre\",1,AUTHORITY[\"EPSG\",\"9001\"]],AXIS[\"Easting\",EAST]," +
        "AXIS[\"Northing\",NORTH],AUTHORITY[\"EPSG\",\"3857\"]]";
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit.Infrastructure;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic;

internal static class CapabilitiesExtentFixture
{
    internal const int ProjectedSrid = 32618;

    internal static TestMetadataV2GraphProvider PublishedLayers(
        string protocol, MetadataV2PublicationType publicationType)
    {
        const string ServiceId = "svc-capabilities-extents";
        var builder = new TestMetadataV2GraphBuilder().AddService(
            ServiceId, "capabilities_extents", protocols: [protocol],
            accessPolicy: new AccessPolicy { AllowAnonymous = true });
        foreach (var (name, layerId, srid) in new[] { ("healthy_extent", 1, 4326), ("projected_extent", 2, ProjectedSrid) })
        {
            var resourceId = "res-" + name;
            var bindingId = "binding-" + name;
            builder.AddResource(resourceId, name,
                    fields:
                    [
                        new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false },
                        new MetadataV2Field { Name = "shape", Type = MetadataV2FieldType.Geometry, Nullable = false }
                    ],
                    accessPolicy: new AccessPolicy { AllowAnonymous = true },
                    spatial: new MetadataV2ResourceSpatial
                    {
                        SpatialReference = new MetadataV2SpatialReference { Srid = srid, Crs = $"EPSG:{srid}", IsGeographic = srid == 4326 },
                        GeometryType = MetadataV2GeometryType.Point,
                        Bbox = srid == 4326
                            ? new MetadataV2Bbox { West = -5, South = 40, East = -4, North = 41 }
                            : new MetadataV2Bbox { West = 500_000, South = 4_100_000, East = 600_000, North = 4_200_000 }
                    })
                .AddStorageBinding(bindingId, resourceId, name, storageLayerId: layerId)
                .AddPublication("pub-" + name, ServiceId, resourceId,
                    layerIndex: layerId, storageBindingId: bindingId,
                    publicationType: publicationType);
        }
        return new TestMetadataV2GraphProvider(builder.Build());
    }

    internal sealed class ExtentTransformProbe(bool extentReadFails) : ICoordinateTransformService
    {
        private int _extentReads;
        public int ExtentReads => Volatile.Read(ref _extentReads);
        public (int Source, int Target) LastCrs { get; private set; }

        public ValueTask<(double MinX, double MinY, double MaxX, double MaxY)?> TransformExtentAsync(
            double minX, double minY, double maxX, double maxY,
            int fromSrid, int toSrid, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _extentReads);
            LastCrs = (fromSrid, toSrid);
            return extentReadFails
                ? ValueTask.FromException<(double, double, double, double)?>(new InvalidOperationException("Extent unavailable"))
                : ValueTask.FromResult<(double, double, double, double)?>((-5d, 40d, -4d, 41d));
        }

        public ValueTask<(double X, double Y)?> TransformPointAsync(
            double x, double y, int fromSrid, int toSrid, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Query;
using Honua.Core.Queries.Filters;
using Honua.Infrastructure.Events;
using Honua.Infrastructure.Validation;
using Honua.Protocols.Ogc.Classic.Wfs20;
using Honua.Protocols.Ogc.Classic.Wfs20.Services;
using Honua.Protocols.Ogc.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

internal static class Wfs20HandlerTestFactory
{
    public static Wfs20Handler Create(IFeatureReader reader, IFeatureWriter writer)
    {
        var coordinateTransform = Substitute.For<ICoordinateTransformService>();
        var queryServices = new Wfs20QueryServices(
            reader,
            Substitute.For<IGmlFeatureStore>(),
            Substitute.For<IMetadataV2GraphProvider>(),
            Substitute.For<IFilterExpressionService>(),
            new Wfs20QueryParameterAdapter(NullLogger<Wfs20QueryParameterAdapter>.Instance),
            Substitute.For<IQueryProcessor>(),
            Options.Create(new Wfs20Options()),
            Substitute.For<IConfiguration>());
        var editServices = new Wfs20EditServices(
            writer,
            new Wfs20EditParameterAdapter(NullLogger<Wfs20EditParameterAdapter>.Instance),
            new EditProcessor(NullLogger<EditProcessor>.Instance),
            new FeatureMutationValidator(Substitute.For<IGeometryValidator>()),
            new FeatureMutationEventService(Substitute.For<IFeatureChangeEventPublisher>()),
            Options.Create(new LimitsOptions()));
        var spatialServices = new Wfs20SpatialServices(
            new OgcFeaturesGeometryServices(
                Substitute.For<IGeometryService>(),
                coordinateTransform,
                Options.Create(new LimitsOptions()),
                NullLogger<OgcFeaturesGeometryServices>.Instance),
            coordinateTransform,
            Substitute.For<ICrsRegistry>());
        return new Wfs20Handler(NullLogger<Wfs20Handler>.Instance, queryServices, editServices, spatialServices);
    }

}

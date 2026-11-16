// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Query;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Queries.Filters;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Events;
using Honua.Infrastructure.Validation;
using Honua.Protocols.Ogc.Classic.Wfs20;
using Honua.Protocols.Ogc.Common;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using MetadataV2ServiceProtocols = Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols;

namespace Honua.Protocols.OgcClassic.UnitTests.Wfs20;

internal static class Wfs20WriteTestHandler
{
    internal static Wfs20Handler Create(
        IMetadataV2GraphProvider provider, IFeatureWriter writer, IFeatureReader reader)
    {
        var coordinateTransform = Substitute.For<ICoordinateTransformService>();
        var queryServices = new Wfs20QueryServices(
            reader,
            Substitute.For<IGmlFeatureStore>(),
            provider,
            Substitute.For<IFilterExpressionService>(),
            new Wfs20QueryParameterAdapter(NullLogger<Wfs20QueryParameterAdapter>.Instance),
            new QueryProcessor(Substitute.For<IFilterExpressionTranslator>(), reader, NullLogger<QueryProcessor>.Instance, provider),
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

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Validation;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Events;
using Honua.Infrastructure.Validation;
using Honua.Protocols.OData;
using Honua.Protocols.OData.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Protocols.OData.UnitTests.Services;

[global::Xunit.Trait("Tier", "Fast")]
public sealed class ODataBatchReadBudgetTests
{
    [Fact]
    public async Task HandleBatchRequest_GetCountAboveConfiguredBudget_Returns400WithoutQuery()
    {
        var options = new ODataOptions();
        var reader = Substitute.For<IFeatureReader>();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<IOptions<ODataOptions>>(Options.Create(options));
        using var serviceProvider = services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = serviceProvider };
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(BatchJson(options.MaxBatchReadOperations + 1)));
        context.Response.Body = new MemoryStream();
        var sut = new ODataBatchOperationHandler(Dependencies(reader), NullLogger<ODataBatchOperationHandler>.Instance);

        var result = await sut.HandleBatchRequestAsync(context);
        await result.ExecuteAsync(context);

        context.Response.Body.Position = 0;
        using var responseReader = new StreamReader(context.Response.Body);
        var body = await responseReader.ReadToEndAsync();
        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest, "response body was {0}", body);
        body.Should().Contain(options.MaxBatchReadOperations.ToString(CultureInfo.InvariantCulture));
        await reader.DidNotReceive().QueryAsync(Arg.Any<int>(), Arg.Any<Honua.Core.Features.FeatureStore.Domain.FeatureQuery>(), Arg.Any<CancellationToken>());
        await reader.DidNotReceive().QueryObjectIdsAsync(Arg.Any<int>(), Arg.Any<Honua.Core.Features.FeatureStore.Domain.FeatureQuery>(), Arg.Any<CancellationToken>());
    }

    private static string BatchJson(int reads)
    {
        var builder = new StringBuilder();
        builder.Append("{\"requests\":[");
        for (var i = 0; i < reads; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append("{\"id\":\"r").Append(i.ToString(CultureInfo.InvariantCulture))
                .Append("\",\"method\":\"GET\",\"url\":\"Features\"}");
        }

        builder.Append("]}");
        return builder.ToString();
    }

    private static ODataBatchDependencies Dependencies(IFeatureReader reader)
        => new(
            reader,
            Substitute.For<IFeatureWriter>(),
            Substitute.For<IGeometryService>(),
            new FeatureMutationValidator(Substitute.For<IGeometryValidator>()),
            Substitute.For<ICrsRegistry>(),
            new EditLimits(),
            new ODataValidationService(
                new CommonQueryValidator(Options.Create(new LimitsOptions())),
                Options.Create(new ODataOptions())),
            new ETagService(),
            new ODataEditParameterAdapter(NullLogger<ODataEditParameterAdapter>.Instance),
            new EditProcessor(NullLogger<EditProcessor>.Instance),
            new FeatureMutationEventService(Substitute.For<IFeatureChangeEventPublisher>()));
}

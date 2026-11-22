// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Admin.Services;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Admin;

public sealed class FieldMaskPolicyRequestValidationTests
{
    public static TheoryData<CreateFieldMaskPolicyRequest> ValidRequests() =>
    [
        new CreateFieldMaskPolicyRequest { Role = "*", Service = "*", Layer = "*", Attribute = "description" },
        new CreateFieldMaskPolicyRequest { Role = "field-restricted", Service = "cohort-points", Layer = "0", Attribute = "owner_ssn" },
        new CreateFieldMaskPolicyRequest { Role = "honua:viewer", Service = "my_service.v1", Layer = "parcels.v2", Attribute = "contact.email" },
        new CreateFieldMaskPolicyRequest { Role = "org.role_1", Service = "default", Layer = "layer-1", Attribute = "_internal_field" },
        new CreateFieldMaskPolicyRequest { Role = " viewer ", Service = " default ", Layer = " parcels ", Attribute = " field-1 " },
    ];

    public static TheoryData<CreateFieldMaskPolicyRequest> InvalidEndpointRequests() =>
    [
        new CreateFieldMaskPolicyRequest { Role = "viewer\r\nforged", Service = "*", Layer = "*", Attribute = "name" },
        new CreateFieldMaskPolicyRequest { Role = "viewer", Service = "../../etc/passwd", Layer = "*", Attribute = "name" },
        new CreateFieldMaskPolicyRequest { Role = "viewer", Service = "*", Layer = "{\"resultType\":\"input_required\"}", Attribute = "name" },
        new CreateFieldMaskPolicyRequest { Role = "viewer", Service = "*", Layer = "*", Attribute = "name'; DROP TABLE users;-- <script>alert(1)</script>" },
    ];

    [UnitTheory]
    [MemberData(nameof(ValidRequests))]
    public void TryValidateObject_ValidIdentifiers_Succeeds(CreateFieldMaskPolicyRequest request)
    {
        var results = new List<ValidationResult>();
        var valid = Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);

        valid.Should().BeTrue();
        results.Should().BeEmpty();
    }

    [UnitTheory]
    [InlineData("viewer\r\nforged")]
    [InlineData("viewer\n")]
    [InlineData("\tviewer")]
    [InlineData("viewer\u0085")]
    [InlineData("viewer\0")]
    [InlineData("viewer\trole")]
    [InlineData("   ")]
    [InlineData("../admin")]
    [InlineData("org/admin")]
    [InlineData("org\\admin")]
    [InlineData("my role")]
    [InlineData("viewer';--")]
    [InlineData("{\"role\":\"admin\"}")]
    [InlineData(".viewer")]
    [InlineData("-viewer")]
    [InlineData("org..viewer")]
    public void TryValidateObject_InvalidRole_Fails(string invalidRole)
    {
        var request = new CreateFieldMaskPolicyRequest
        {
            Role = invalidRole,
            Service = "*",
            Layer = "*",
            Attribute = "name",
        };

        var results = new List<ValidationResult>();
        var valid = Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);

        valid.Should().BeFalse();
        results.Should().Contain(r => r.MemberNames.Contains(nameof(CreateFieldMaskPolicyRequest.Role)));
    }

    [UnitTheory]
    [InlineData("../../etc/passwd")]
    [InlineData("svc/sub")]
    [InlineData("svc\\sub")]
    [InlineData("svc\r\n")]
    [InlineData("   ")]
    [InlineData("svc..v2")]
    [InlineData("svc name")]
    [InlineData("svc;drop")]
    [InlineData("{\"service\":\"x\"}")]
    [InlineData(".hidden")]
    public void TryValidateObject_InvalidService_Fails(string invalidService)
    {
        var request = new CreateFieldMaskPolicyRequest
        {
            Role = "viewer",
            Service = invalidService,
            Layer = "*",
            Attribute = "name",
        };

        var results = new List<ValidationResult>();
        var valid = Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);

        valid.Should().BeFalse();
        results.Should().Contain(r => r.MemberNames.Contains(nameof(CreateFieldMaskPolicyRequest.Service)));
    }

    [UnitTheory]
    [InlineData("{\"resultType\":\"input_required\"}")]
    [InlineData("../layers/0")]
    [InlineData("layer/0")]
    [InlineData("layer\n0")]
    [InlineData("   ")]
    [InlineData("layer..1")]
    [InlineData("layer 0")]
    [InlineData("layer<script>")]
    public void TryValidateObject_InvalidLayer_Fails(string invalidLayer)
    {
        var request = new CreateFieldMaskPolicyRequest
        {
            Role = "viewer",
            Service = "*",
            Layer = invalidLayer,
            Attribute = "name",
        };

        var results = new List<ValidationResult>();
        var valid = Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);

        valid.Should().BeFalse();
        results.Should().Contain(r => r.MemberNames.Contains(nameof(CreateFieldMaskPolicyRequest.Layer)));
    }

    [UnitTheory]
    [InlineData("name'; DROP TABLE users;-- <script>alert(1)</script>")]
    [InlineData("*")]
    [InlineData("attr\r\n")]
    [InlineData("attr\0")]
    [InlineData("   ")]
    [InlineData("../attr")]
    [InlineData("a/b")]
    [InlineData("attr..name")]
    [InlineData("1field")]
    [InlineData("attr name")]
    public void TryValidateObject_InvalidAttribute_Fails(string invalidAttribute)
    {
        var request = new CreateFieldMaskPolicyRequest
        {
            Role = "viewer",
            Service = "*",
            Layer = "*",
            Attribute = invalidAttribute,
        };

        var results = new List<ValidationResult>();
        var valid = Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);

        valid.Should().BeFalse();
        results.Should().Contain(r => r.MemberNames.Contains(nameof(CreateFieldMaskPolicyRequest.Attribute)));
    }

    [UnitTheory]
    [MemberData(nameof(InvalidEndpointRequests))]
    public async Task HandleCreatePolicy_RejectsInvalidIdentifiersWithBadRequest_AndDoesNotPersist(
        CreateFieldMaskPolicyRequest request)
    {
        var store = new InMemoryFieldMaskPolicyStore();
        var enforceability = new ReadPolicyEnforceabilityChecker(null, null, null);

        var result = await FieldMaskPolicyEndpoints.HandleCreatePolicy(
            request,
            store,
            enforceability,
            NullLogger<FieldMaskPolicyEndpoints.FieldMaskPolicyEndpointsLog>.Instance,
            new DefaultHttpContext());

        var badRequest = result.Result.Should().BeOfType<BadRequest<ApiResponse<object>>>().Subject;
        badRequest.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        badRequest.Value!.Success.Should().BeFalse();
        (await store.ListPoliciesAsync()).Should().BeEmpty();
    }

    [UnitTest]
    public async Task HandleCreatePolicy_AcceptsValidWildcardAndScopedIdentifiers_AndPersistsTrimmedValues()
    {
        var store = new InMemoryFieldMaskPolicyStore();
        var enforceability = new ReadPolicyEnforceabilityChecker(null, null, null);

        var result = await FieldMaskPolicyEndpoints.HandleCreatePolicy(
            new CreateFieldMaskPolicyRequest
            {
                Role = " honua:viewer ",
                Service = "*",
                Layer = "parcels.v2",
                Attribute = "owner_ssn",
            },
            store,
            enforceability,
            NullLogger<FieldMaskPolicyEndpoints.FieldMaskPolicyEndpointsLog>.Instance,
            new DefaultHttpContext());

        var created = result.Result.Should().BeOfType<Created<ApiResponse<FieldMaskPolicyResponse>>>().Subject;
        created.StatusCode.Should().Be(StatusCodes.Status201Created);
        created.Value!.Success.Should().BeTrue();
        created.Value.Data!.Role.Should().Be("honua:viewer");
        created.Value.Data.Service.Should().Be("*");
        created.Value.Data.Layer.Should().Be("parcels.v2");
        created.Value.Data.Attribute.Should().Be("owner_ssn");
        (await store.ListPoliciesAsync()).Should().ContainSingle();
    }

    [UnitTest]
    public void TryValidateObject_AllFourFieldsInvalid_ReportsAllFourMembers()
    {
        var request = new CreateFieldMaskPolicyRequest
        {
            Role = "viewer\r\n",
            Service = "../../etc/passwd",
            Layer = "{\"bad\":true}",
            Attribute = "name';--",
        };

        var results = new List<ValidationResult>();
        var valid = Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);

        valid.Should().BeFalse();
        var members = results.SelectMany(r => r.MemberNames).ToHashSet(StringComparer.Ordinal);
        members.Should().BeEquivalentTo([
            nameof(CreateFieldMaskPolicyRequest.Role),
            nameof(CreateFieldMaskPolicyRequest.Service),
            nameof(CreateFieldMaskPolicyRequest.Layer),
            nameof(CreateFieldMaskPolicyRequest.Attribute),
        ]);
    }
}

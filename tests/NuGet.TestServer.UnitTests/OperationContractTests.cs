using System.Reflection;
using System.Text.Json;
using NuGet.TestServer.Extensions.Abstractions;

namespace NuGet.TestServer.UnitTests;

public sealed class OperationContractTests
{
    private static readonly string[] StableOperationIds =
    [
        "NuGet.FlatContainer.GetHash",
        "NuGet.FlatContainer.GetNuspec",
        "NuGet.FlatContainer.GetPackage",
        "NuGet.FlatContainer.GetSymbol",
        "NuGet.FlatContainer.GetVersions",
        "NuGet.PackageManagement.Delete",
        "NuGet.PackageManagement.List",
        "NuGet.PackageManagement.Push",
        "NuGet.PackageManagement.PushSymbols",
        "NuGet.PackageManagement.Relist",
        "NuGet.PackageManagement.Unlist",
        "NuGet.Registration.GetIndex",
        "NuGet.Registration.GetLeaf",
        "NuGet.Registration.GetPage",
        "NuGet.Search.Query",
        "NuGet.ServiceIndex.Get",
        "NuGet.Vulnerabilities.GetIndex",
        "NuGet.Vulnerabilities.GetPage",
        "NuTest.Backup.Create",
        "NuTest.Control.AddFault",
        "NuTest.Control.AddPackage",
        "NuTest.Control.ClearFaults",
        "NuTest.Control.ClearRequests",
        "NuTest.Control.DeletePackage",
        "NuTest.Control.GetFaults",
        "NuTest.Control.GetPackages",
        "NuTest.Control.GetRequests",
        "NuTest.Control.GetState",
        "NuTest.Control.RelistPackage",
        "NuTest.Control.Reset",
        "NuTest.Control.UnlistPackage",
        "NuTest.Control.UpdatePackageMetadata",
        "NuTest.Diagnostics.Get",
        "NuTest.Health.GetLiveness",
        "NuTest.Health.GetReadiness",
        "NuTest.Health.GetStorage",
        "NuTest.Moderation.GetAudit",
        "NuTest.Moderation.GetValidations",
        "NuTest.Moderation.Moderate",
        "NuTest.Restore.Execute"
    ];

    [Fact]
    public void Operation_ids_are_unique_and_match_the_pre_compatibility_snapshot()
    {
        var ids = OperationContracts.All
            .Select(contract => contract.Id.Value)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(StableOperationIds, ids);
    }

    [Fact]
    public void Every_current_endpoint_family_has_typed_contracts()
    {
        var expected = typeof(OperationFamily)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(property => property.PropertyType == typeof(OperationFamily))
            .Select(property => Assert.IsType<OperationFamily>(property.GetValue(null)))
            .ToArray();
        var actual = OperationContracts.All.Select(contract => contract.Family).Distinct();

        Assert.Empty(expected.Except(actual));
        Assert.All(OperationContracts.All, contract =>
        {
            Assert.Equal(1, contract.ContractVersion);
            Assert.EndsWith(".v1", contract.RequestContract, StringComparison.Ordinal);
            Assert.EndsWith(".v1", contract.ResponseContract, StringComparison.Ordinal);
        });
        Assert.All(OperationContracts.Bindings, binding =>
        {
            Assert.EndsWith("Request", binding.RequestType.Name, StringComparison.Ordinal);
            Assert.EndsWith("Response", binding.ResponseType.Name, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Read_contract_round_trips_through_json()
    {
        var value = new GetPackageResponse(
            new ContentDescriptor(
                new StreamHandle("stream-1", 4096, "application/octet-stream"),
                "sha512-value",
                1024,
                SupportsRanges: true));

        var roundTrip = JsonSerializer.Deserialize<GetPackageResponse>(
            JsonSerializer.Serialize(value));

        Assert.Equal(value, roundTrip);
    }

    [Fact]
    public void Route_references_round_trip_through_json()
    {
        var value = RouteReference.Endpoint(
            "registration.leaf",
            RouteParameterValue.PackageId("id", "Example.Package"),
            RouteParameterValue.PackageVersion("version", "1.0.0"));

        var roundTrip = JsonSerializer.Deserialize<RouteReference>(
            JsonSerializer.Serialize(value));

        Assert.NotNull(roundTrip);
        Assert.Equal(value.RouteName, roundTrip.RouteName);
        Assert.Equal(value.Target, roundTrip.Target);
        Assert.Equal(value.Parameters.ToArray(), roundTrip.Parameters.ToArray());
        Assert.Equal(value.Query.ToArray(), roundTrip.Query.ToArray());
        Assert.Equal(value.Fragment, roundTrip.Fragment);
    }

    [Fact]
    public void Mutation_contract_and_error_round_trip_through_json()
    {
        var value = OperationResponse<PushPackageResponse>.Failure(
            new OperationError(
                OperationErrorCodes.PolicyDenied,
                "Package policy denied publication.",
                retryAfterSeconds: null));

        var roundTrip = JsonSerializer.Deserialize<OperationResponse<PushPackageResponse>>(
            JsonSerializer.Serialize(value));

        Assert.Equal(value, roundTrip);
    }

    [Fact]
    public void Response_envelope_rejects_ambiguous_states()
    {
        var response = new PushPackageResponse(
            new PackageIdentity("Example.Package", "1.2.3"),
            PublicationOutcome.Quarantined);
        var error = new OperationError(
            OperationErrorCodes.PolicyDenied,
            "Package policy denied publication.",
            retryAfterSeconds: null);

        Assert.Throws<ArgumentException>(() => new OperationResponse<PushPackageResponse>(
            response,
            error));
        Assert.Throws<ArgumentException>(() => new OperationResponse<PushPackageResponse>(
            null,
            null));
    }

    [Fact]
    public void Operation_descriptors_round_trip_without_clr_types()
    {
        var json = JsonSerializer.Serialize(OperationContracts.All);
        var roundTrip = JsonSerializer.Deserialize<OperationContract[]>(json);

        Assert.Equal(OperationContracts.All, roundTrip);
        Assert.DoesNotContain("System.Type", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Registration_and_search_contracts_cover_current_protocol_fields()
    {
        string[] registrationFields =
        [
            "Id", "Registration", "PackageContent", "Package", "Authors",
            "Owners", "Downloads", "Description", "Summary", "Title", "Tags",
            "ProjectUrl", "Readme", "Icon", "LicenseExpression", "LicenseFile",
            "LicenseUrl", "PackageTypes", "Repository", "Listed", "Published",
            "DependencyGroups", "Deprecation", "Vulnerabilities"
        ];
        string[] searchFields =
        [
            "Id", "Registration", "Package", "Description", "Summary", "Title",
            "Tags", "Authors", "Owners", "ProjectUrl", "TotalDownloads", "Verified",
            "PackageTypes", "Versions"
        ];

        Assert.Equal(
            registrationFields.Order(),
            typeof(RegistrationLeafDocument).GetProperties().Select(property => property.Name).Order());
        Assert.Equal(
            searchFields.Order(),
            typeof(SearchResultDocument).GetProperties().Select(property => property.Name).Order());
        Assert.Equal(
            typeof(string),
            typeof(VulnerabilityAdvisoryDocument).GetProperty("Severity")!.PropertyType);
        Assert.Equal(
            ["Count", "Id", "Items"],
            typeof(GetRegistrationIndexResponse)
                .GetProperties()
                .Select(property => property.Name)
                .Order());
        Assert.Equal(
            ["Count", "Id", "Items", "Lower", "Parent", "Upper"],
            typeof(RegistrationPageDocument)
                .GetProperties()
                .Select(property => property.Name)
                .Order());
    }

    [Fact]
    public void Extension_contracts_do_not_expose_host_derived_url_inputs_or_outputs()
    {
        var properties = typeof(OperationContracts).Assembly
            .GetTypes()
            .Where(type => type.Namespace == typeof(OperationContracts).Namespace)
            .SelectMany(type => type.GetProperties())
            .ToArray();

        Assert.DoesNotContain(properties, property =>
            property.Name.Equals("BaseAddress", StringComparison.Ordinal) ||
            property.Name.EndsWith("IdUrl", StringComparison.Ordinal) ||
            property.Name.EndsWith("RegistrationUrl", StringComparison.Ordinal) ||
            property.Name.EndsWith("PackageContentUrl", StringComparison.Ordinal));
        Assert.Equal(typeof(RouteReference), typeof(ServiceResourceDescriptor).GetProperty("Route")!.PropertyType);
        Assert.Equal(typeof(RouteReference), typeof(RegistrationLeafDocument).GetProperty("Id")!.PropertyType);
        Assert.Equal(typeof(RouteReference), typeof(RegistrationLeafDocument).GetProperty("Registration")!.PropertyType);
        Assert.Equal(typeof(RouteReference), typeof(RegistrationLeafDocument).GetProperty("PackageContent")!.PropertyType);
        Assert.Equal(typeof(RouteReference), typeof(VulnerabilityPageDescriptor).GetProperty("Route")!.PropertyType);
    }

    [Fact]
    public void Successful_mutation_envelope_round_trips_through_json()
    {
        var value = OperationResponse<PushPackageResponse>.Success(
            new PushPackageResponse(
                new PackageIdentity("Example.Package", "1.2.3"),
                PublicationOutcome.Quarantined));

        var roundTrip = JsonSerializer.Deserialize<OperationResponse<PushPackageResponse>>(
            JsonSerializer.Serialize(value));

        Assert.Equal(value, roundTrip);
    }

    [Theory]
    [InlineData(OperationErrorCodes.InvalidRequest, (int)OperationErrorKind.InvalidRequest)]
    [InlineData(OperationErrorCodes.NotFound, (int)OperationErrorKind.NotFound)]
    [InlineData(OperationErrorCodes.Conflict, (int)OperationErrorKind.Conflict)]
    [InlineData(OperationErrorCodes.PolicyDenied, (int)OperationErrorKind.Forbidden)]
    [InlineData(OperationErrorCodes.LimitExceeded, (int)OperationErrorKind.LimitExceeded)]
    [InlineData(OperationErrorCodes.Unavailable, (int)OperationErrorKind.Unavailable)]
    [InlineData("unknown.error", (int)OperationErrorKind.Internal)]
    public void Error_codes_map_to_stable_failure_kinds(string code, int expected)
    {
        Assert.Equal((OperationErrorKind)expected, OperationErrorCodes.Classify(code));
    }

    [Fact]
    public void Error_kind_is_derived_from_code_during_deserialization()
    {
        const string json =
            """{"Code":"resource.not-found","Kind":2,"Message":"missing","RetryAfterSeconds":null}""";

        var error = JsonSerializer.Deserialize<OperationError>(json);

        Assert.Equal(OperationErrorKind.NotFound, error!.Kind);
        Assert.DoesNotContain(
            "\"Kind\"",
            JsonSerializer.Serialize(error),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Serialized_contract_collections_are_immutable()
    {
        var mutableCollectionTypes = new[]
        {
            typeof(List<>),
            typeof(IList<>),
            typeof(ICollection<>),
            typeof(IReadOnlyList<>),
            typeof(IReadOnlyCollection<>)
        };
        var contractProperties = typeof(OperationContracts).Assembly
            .GetTypes()
            .Where(type =>
                type.Namespace == typeof(OperationContracts).Namespace &&
                type != typeof(OperationContracts))
            .SelectMany(type => type.GetProperties());

        Assert.DoesNotContain(contractProperties, property =>
            property.PropertyType.IsGenericType &&
            mutableCollectionTypes.Contains(property.PropertyType.GetGenericTypeDefinition()));
    }

    [Fact]
    public void Extension_point_methods_are_async_and_cancellation_aware()
    {
        Type[] interfaces =
        [
            typeof(IOperationHandler<,>),
            typeof(IOperationOwner<,>),
            typeof(IOperationValidator<>),
            typeof(IDocumentContributor<>),
            typeof(IPolicyParticipant<>)
        ];

        foreach (var method in interfaces
                     .SelectMany(type => type.GetMethods())
                     .Where(method => !method.IsSpecialName))
        {
            Assert.True(
                method.ReturnType.IsGenericType &&
                method.ReturnType.GetGenericTypeDefinition() == typeof(ValueTask<>),
                $"{method.DeclaringType}.{method.Name} must return ValueTask<T>.");
            Assert.Equal(typeof(CancellationToken), method.GetParameters()[^1].ParameterType);
        }
    }

    [Fact]
    public void Document_contributors_receive_only_a_declared_extension_slot()
    {
        var method = typeof(IDocumentContributor<>).GetMethod("ContributeAsync")!;

        Assert.Equal(typeof(DocumentContributionContext), method.GetParameters()[0].ParameterType);
    }

    [Fact]
    public void Contracts_are_internal_and_marked_pre_compatibility()
    {
        var assembly = typeof(OperationContracts).Assembly;
        var status = assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "CompatibilityStatus");

        Assert.Equal("InternalPreCompatibility", status.Value);
        Assert.DoesNotContain(
            assembly.GetTypes(),
            type => type.IsPublic || type.IsNestedPublic);
    }

    [Fact]
    public void Contract_assembly_has_no_runtime_or_implementation_dependencies()
    {
        var assembly = typeof(OperationContracts).Assembly;
        var forbiddenReferences = new[]
        {
            "Microsoft.AspNetCore",
            "Microsoft.Extensions.DependencyInjection",
            "Microsoft.Data.Sqlite",
            "NuGet.TestServer"
        };
        var references = assembly.GetReferencedAssemblies().Select(reference => reference.Name!);

        Assert.DoesNotContain(
            references,
            reference => forbiddenReferences.Any(forbidden =>
                reference.StartsWith(forbidden, StringComparison.Ordinal)));

        var forbiddenTypeNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "Microsoft.AspNetCore.Http.HttpContext",
            "Microsoft.AspNetCore.Http.IResult",
            "System.IServiceProvider",
            "System.IO.Stream",
            "System.IO.FileInfo",
            "System.IO.DirectoryInfo"
        };
        foreach (var property in assembly.GetTypes().SelectMany(type => type.GetProperties()))
        {
            Assert.DoesNotContain(
                EnumerateContractTypes(property.PropertyType),
                type => forbiddenTypeNames.Contains(type.FullName ?? string.Empty));
            Assert.DoesNotMatch(
                "(?i)(path|directory|secret|password|apikey)$",
                property.Name);
        }
    }

    private static IEnumerable<Type> EnumerateContractTypes(Type type)
    {
        yield return type;
        if (type.IsArray)
        {
            foreach (var element in EnumerateContractTypes(type.GetElementType()!))
            {
                yield return element;
            }
        }

        foreach (var argument in type.GetGenericArguments())
        {
            foreach (var nested in EnumerateContractTypes(argument))
            {
                yield return nested;
            }
        }
    }
}

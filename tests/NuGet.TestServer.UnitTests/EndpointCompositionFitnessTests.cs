using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NuGet.TestServer.Authentication;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Kernel;
using NuGet.TestServer.Kernel.Routing;

namespace NuGet.TestServer.UnitTests;

/// <summary>
/// Architecture fitness for Step 11A. Exactly one kernel mapper may create ASP.NET
/// endpoints, and the descriptor surface stays transport-neutral.
/// </summary>
public sealed class EndpointCompositionFitnessTests
{
    [Fact]
    public void Only_the_kernel_mapper_creates_asp_net_endpoints()
    {
        var offenders = new List<string>();
        var pattern = new Regex(
            @"\.Map(Get|Post|Put|Delete|Patch|Methods|Group|Fallback)\s*\(",
            RegexOptions.CultureInvariant);

        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(RepositoryRoot, "src"),
                     "*.cs",
                     SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal) ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            if (pattern.IsMatch(text) || text.Contains("IEndpointRouteBuilder", StringComparison.Ordinal))
            {
                offenders.Add(Path.GetFileName(file));
            }
        }

        Assert.Equal(["KernelEndpointMapper.cs"], offenders.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Descriptor_surface_never_exposes_hosting_or_dependency_injection_types()
    {
        Type[] surface =
        [
            typeof(EndpointDescriptor),
            typeof(EndpointRequest),
            typeof(EndpointInvocation),
            typeof(IEndpointHandler),
            typeof(EndpointLimits),
            typeof(EndpointAccessPolicy),
            typeof(EndpointCaller)
        ];

        foreach (var type in surface)
        {
            foreach (var member in type.GetMembers(
                         BindingFlags.Public | BindingFlags.NonPublic |
                         BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                foreach (var used in DescribeTypes(member))
                {
                    Assert.False(
                        IsForbidden(used),
                        $"{type.Name}.{member.Name} exposes '{used.FullName}'.");
                }
            }
        }
    }

    [Fact]
    public void Every_mapped_endpoint_comes_from_the_frozen_route_table()
    {
        using var host = TestServerApplication.Build(ServerProfiles.Embedded);
        var table = host.Services.GetRequiredService<KernelRouteTable>();

        var mapped = ((IEndpointRouteBuilder)host.Application).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();

        Assert.Equal(table.Endpoints.Length, mapped.Length);
        foreach (var endpoint in mapped)
        {
            var route = endpoint.Metadata.GetMetadata<KernelRouteEndpoint>();
            Assert.NotNull(route);
            Assert.Contains(table.Endpoints, entry => entry.Descriptor.Name == route!.Descriptor.Name);
            Assert.Equal(route!.Descriptor.PathTemplate, endpoint.RoutePattern.RawText);
            Assert.NotNull(endpoint.Metadata.GetMetadata<NuGetAccessRequirement>());
            Assert.Equal(
                route.Access,
                endpoint.Metadata.GetMetadata<NuGetAccessRequirement>());
            Assert.Equal(
                route.Descriptor.Operations.Select(operation => operation.OperationId).ToArray(),
                endpoint.Metadata.GetMetadata<OperationRouteMetadata>()!.OperationIds.ToArray());
            Assert.Equal(
                route.Descriptor.Methods.ToArray(),
                endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.ToArray());
        }
    }

    [Fact]
    public void Parallel_hosts_own_independent_but_identical_route_tables()
    {
        using var first = TestServerApplication.Build(ServerProfiles.Embedded);
        using var second = TestServerApplication.Build(ServerProfiles.Embedded);

        var firstTable = first.Services.GetRequiredService<KernelRouteTable>();
        var secondTable = second.Services.GetRequiredService<KernelRouteTable>();

        Assert.NotSame(firstTable, secondTable);
        Assert.Equal(
            firstTable.Endpoints.Select(endpoint => endpoint.Descriptor.Name),
            secondTable.Endpoints.Select(endpoint => endpoint.Descriptor.Name));
        Assert.Equal(firstTable.Diagnostics, secondTable.Diagnostics);
    }

    private static bool IsForbidden(Type type)
    {
        var namespaceName = (type.IsArray || type.IsByRef
            ? type.GetElementType()!
            : type).Namespace ?? string.Empty;
        return namespaceName.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) ||
               namespaceName.StartsWith("Microsoft.Extensions.DependencyInjection", StringComparison.Ordinal) ||
               namespaceName.StartsWith("Microsoft.Extensions.Hosting", StringComparison.Ordinal);
    }

    private static IEnumerable<Type> DescribeTypes(MemberInfo member) => member switch
    {
        MethodInfo method =>
            method.GetParameters().Select(parameter => parameter.ParameterType)
                .Append(method.ReturnType)
                .SelectMany(Expand),
        ConstructorInfo constructor =>
            constructor.GetParameters().Select(parameter => parameter.ParameterType)
                .SelectMany(Expand),
        PropertyInfo property => Expand(property.PropertyType),
        FieldInfo field => Expand(field.FieldType),
        _ => []
    };

    private static IEnumerable<Type> Expand(Type type) =>
        type.IsGenericType
            ? type.GetGenericArguments().Append(type.GetGenericTypeDefinition())
            : [type];

    private static string RepositoryRoot { get; } = FindRepositoryRoot();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "NuGet.TestServer.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ??
               throw new InvalidOperationException("The repository root was not found.");
    }
}

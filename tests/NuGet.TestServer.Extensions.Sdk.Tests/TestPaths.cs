using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace NuGet.TestServer.Extensions.Sdk.Tests;

internal static class TestPaths
{
    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    public static string Fixture(string name) =>
        Path.Combine(RepositoryRoot, "tests", "NuGet.TestServer.Extensions.Sdk.Tests", "Fixtures", name);

    public static string Snapshot(string name) =>
        Path.Combine(RepositoryRoot, "tests", "NuGet.TestServer.Extensions.Sdk.Tests", "Snapshots", name);

    public static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static async Task<ProcessResult> DotNetAsync(params string[] arguments)
    {
        var artifacts = Path.Combine(RepositoryRoot, "artifacts", "step19-sdk-tests");
        Directory.CreateDirectory(artifacts);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = RepositoryRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.StartInfo.Environment["NUGET_PACKAGES"] =
            Path.Combine(artifacts, "packages");
        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, output + error);
    }

    public static string[] PackageEntries(string packagePath)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        return archive.Entries
            .Select(entry => entry.FullName)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    public static string PublicApi(Assembly assembly)
    {
        var lines = assembly.GetExportedTypes()
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .SelectMany(type =>
            {
                var members = type.GetMembers(
                        BindingFlags.Public | BindingFlags.Instance |
                        BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Where(member => member.MemberType is MemberTypes.Method
                        or MemberTypes.Property
                        or MemberTypes.Field)
                    .Where(member => !type.IsEnum || member.MemberType != MemberTypes.Field)
                    .Where(member => member is not MethodInfo method ||
                                     (!method.IsSpecialName &&
                                      !method.Name.StartsWith('<') &&
                                      method.Name is not "Equals"
                                          and not "GetHashCode"
                                          and not "ToString"
                                          and not "Deconstruct"))
                    .Select(member => $"  {MemberSignature(member)}")
                    .Order(StringComparer.Ordinal);
                return new[] { $"type {TypeName(type)}" }.Concat(members);
            });
        return string.Join('\n', lines) + "\n";
    }

    public static string NormalizePublicApi(string value) =>
        string.Join(
            '\n',
            value.ReplaceLineEndings("\n")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Order(StringComparer.Ordinal)) + "\n";

    private static string MemberSignature(MemberInfo member) => member switch
    {
        MethodInfo method =>
            $"{TypeName(method.ReturnType)} {MethodName(method)}(" +
            $"{string.Join(',', method.GetParameters().Select(Parameter))})",
        PropertyInfo property => $"{TypeName(property.PropertyType)} {property.Name} {{ get; }}",
        FieldInfo field => $"{TypeName(field.FieldType)} {field.Name}",
        _ => member.Name
    };

    private static string Parameter(ParameterInfo parameter) =>
        $"{TypeName(parameter.ParameterType)} {parameter.Name}";

    private static string MethodName(MethodInfo method) =>
        method.IsGenericMethodDefinition
            ? $"{method.Name}<{string.Join(',', method.GetGenericArguments().Select(type => type.Name))}>"
            : method.Name;

    private static string TypeName(Type type)
    {
        if (type.IsArray)
        {
            return $"{TypeName(type.GetElementType()!)}[]";
        }

        if (!type.IsGenericType)
        {
            return type.FullName ?? type.Name;
        }

        var name = (type.GetGenericTypeDefinition().FullName ?? type.Name)
            .Split('`')[0];
        return $"{name}<{string.Join(',', type.GetGenericArguments().Select(TypeName))}>";
    }

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

internal sealed record ProcessResult(int ExitCode, string Output);

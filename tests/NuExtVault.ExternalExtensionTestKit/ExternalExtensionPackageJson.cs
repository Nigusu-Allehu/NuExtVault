using System.Text.Json;

namespace NuExtVault.ExternalExtensionTestKit;

/// <summary>
/// Step 20 tests-first red phase helper. Builds `extension-package.json` bytes
/// (assumed schema v1 — see .design/microkernel-step20-external-extension-tests.md):
/// entry assembly/type plus half-open-range extension dependencies.
/// </summary>
public static class ExternalExtensionPackageJson
{
    public const string SchemaUri = "https://schemas.nuextvault.dev/extensions/package/v1";

    public static byte[] Build(
        string entryAssembly,
        string entryType,
        IReadOnlyList<ExternalExtensionDependencySpec> dependencies)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("$schema", SchemaUri);
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("entryAssembly", entryAssembly);
            writer.WriteString("entryType", entryType);
            writer.WritePropertyName("dependencies");
            writer.WriteStartArray();
            foreach (var dependency in dependencies)
            {
                writer.WriteStartObject();
                writer.WriteString("id", dependency.ExtensionId);
                writer.WriteString("minimumInclusive", dependency.MinimumInclusive);
                writer.WriteString("maximumExclusive", dependency.MaximumExclusive);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }
}

/// <summary>One declared extension-to-extension dependency with a half-open
/// version range `[MinimumInclusive, MaximumExclusive)`.</summary>
public sealed record ExternalExtensionDependencySpec(
    string ExtensionId,
    string MinimumInclusive,
    string MaximumExclusive);

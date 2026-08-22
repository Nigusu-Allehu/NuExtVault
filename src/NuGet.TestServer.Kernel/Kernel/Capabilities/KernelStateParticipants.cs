using System.Collections.Immutable;
using NuGet.TestServer.Hosting;
using NuGet.TestServer.Extensions.Abstractions;

namespace NuGet.TestServer.Kernel.Capabilities;

/// <summary>
/// The single declaration of the built-in extension state participants. Composition
/// registers these with the live store and offline backup validation compares a manifest
/// against the same set, so the active server and a restore can never disagree.
/// </summary>
internal static class KernelStateParticipants
{
    internal const string VulnerabilitySchemaName = "vulnerability-snapshot";

    public static ImmutableArray<StateParticipantDescriptor> BuiltIn { get; } =
    [
        // The snapshot is optional state: the extension starts without it, adopts the legacy
        // cache when one exists, and otherwise serves the embedded snapshot and refreshes in
        // the background. Marking it required would reject an otherwise valid backup that
        // simply never persisted a snapshot. Extensions whose state cannot be rebuilt declare
        // Required: true, which the store, restore, and backup validation all enforce.
        new StateParticipantDescriptor(
            BuiltInExtensionIds.Vulnerabilities,
            ExtensionVersion: "1.0.0",
            VulnerabilitySchemaName,
            SchemaVersion: 1,
            Required: false)
    ];
}

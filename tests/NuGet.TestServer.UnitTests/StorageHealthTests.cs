using NuGet.TestServer.Operations;

namespace NuGet.TestServer.UnitTests;

public sealed class StorageHealthTests
{
    [Fact]
    public void Vulnerability_inventory_reports_single_owner_state_and_legacy_entries()
    {
        using var storage = new TemporaryDirectory();
        var stateFile = Path.Combine(
            storage.Path,
            "extension-state",
            "owner",
            "snapshot.json");
        Directory.CreateDirectory(Path.GetDirectoryName(stateFile)!);
        File.WriteAllText(stateFile, "{}");
        Directory.CreateDirectory(Path.Combine(storage.Path, "vulnerabilities", "first"));
        Directory.CreateDirectory(Path.Combine(storage.Path, "vulnerabilities", "second"));
        Directory.CreateDirectory(Path.Combine(storage.Path, "vulnerabilities", ".partial.tmp"));

        var report = new StorageHealth(storage.Path).GetReport();

        Assert.Equal(3, report.VulnerabilitySnapshotCount);
        Assert.Equal(1, report.VulnerabilitySnapshotRetentionLimit);
    }
}

using System.Text.Json;
using System.Text;

namespace NuGet.TestServer.Authentication;

public enum SecurityAuditEventType
{
    AuthenticationSucceeded,
    AuthenticationFailed,
    AuthenticationThrottled,
    AuthorizationDenied,
    PackageOwnershipClaimed
}

public sealed record SecurityAuditEvent(
    DateTimeOffset Timestamp,
    SecurityAuditEventType EventType,
    string Client,
    string? Identity,
    string Method,
    string Path,
    string? Detail);

public interface ISecurityAuditSink
{
    void Write(SecurityAuditEvent auditEvent);
    IReadOnlyList<SecurityAuditEvent> GetAll();
}

public sealed class SecurityAuditSink : ISecurityAuditSink
{
    private const int MaximumRetainedEvents = 1_000;
    private const long MaximumAuditFileBytes = 10 * 1024 * 1024;
    private readonly Queue<SecurityAuditEvent> _events = new();
    private readonly string? _filePath;
    private readonly object _lock = new();

    public SecurityAuditSink(string? storageDirectory)
    {
        if (storageDirectory is not null)
        {
            var securityDirectory = Path.Combine(storageDirectory, "security");
            Directory.CreateDirectory(securityDirectory);
            _filePath = Path.Combine(securityDirectory, "audit.jsonl");
        }
    }

    public void Write(SecurityAuditEvent auditEvent)
    {
        lock (_lock)
        {
            _events.Enqueue(auditEvent);
            while (_events.Count > MaximumRetainedEvents)
            {
                _events.Dequeue();
            }

            if (_filePath is null)
            {
                return;
            }

            var line = JsonSerializer.Serialize(auditEvent) + Environment.NewLine;
            if (File.Exists(_filePath) &&
                new FileInfo(_filePath).Length + Encoding.UTF8.GetByteCount(line) >
                MaximumAuditFileBytes)
            {
                File.Move(_filePath, _filePath + ".1", overwrite: true);
            }

            File.AppendAllText(_filePath, line);
        }
    }

    public IReadOnlyList<SecurityAuditEvent> GetAll()
    {
        lock (_lock)
        {
            return _events.ToArray();
        }
    }
}

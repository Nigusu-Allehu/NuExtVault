using System.Text.Json;
using NuGet.TestServer.Extensions.Sdk;

namespace NuTest.PackageStaging;

/// <summary>
/// Bounded option parsing for staging routes. Every read is defensive: a missing,
/// empty, or malformed body yields defaults instead of an exception, so a malformed
/// request can never turn into an unhandled failure.
/// </summary>
internal static class StagingJson
{
    private const string IdempotencyHeader = "Idempotency-Key";
    private const int MaximumKeyLength = 128;

    internal sealed record GroupOptions(int? MaximumPackages, int? TtlMinutes);

    internal static GroupOptions ReadGroupOptions(BoundedDocument body)
    {
        if (body.ContentLength == 0)
        {
            return new GroupOptions(null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(
                body.Content,
                new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new GroupOptions(null, null);
            }

            return new GroupOptions(
                ReadInt32(document.RootElement, "maximumPackages"),
                ReadInt32(document.RootElement, "ttlMinutes"));
        }
        catch (JsonException)
        {
            return new GroupOptions(null, null);
        }
    }

    internal static string ReadReason(BoundedDocument body)
    {
        if (body.ContentLength == 0)
        {
            return "rejected";
        }

        try
        {
            using var document = JsonDocument.Parse(
                body.Content,
                new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("reason", out var reason) &&
                reason.ValueKind == JsonValueKind.String &&
                reason.GetString() is { Length: > 0 } value)
            {
                return value.Length > 256 ? value[..256] : value;
            }
        }
        catch (JsonException)
        {
            // A malformed body still yields the default reason.
        }

        return "rejected";
    }

    /// <summary>
    /// Reads the idempotency key from the declared header, falling back to the query
    /// value so both client styles work.
    /// </summary>
    internal static string? ReadIdempotencyKey(RouteBindingRequest request)
    {
        var value = request.FindHeader(IdempotencyHeader);
        if (string.IsNullOrWhiteSpace(value) &&
            request.TryGetQuery("idempotencyKey", out var query))
        {
            value = query;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Length > MaximumKeyLength ? value[..MaximumKeyLength] : value;
    }

    internal static int ReadTake(RouteBindingRequest request) =>
        request.TryGetQuery("take", out var take) &&
        int.TryParse(take, out var parsed)
            ? parsed
            : 0;

    private static int? ReadInt32(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var parsed)
            ? parsed
            : null;
}

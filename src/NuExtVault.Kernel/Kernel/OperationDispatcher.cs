using System.Diagnostics;
using Microsoft.Data.Sqlite;
using NuExtVault.Extensions.Sdk;
using NuExtVault.Kernel.Capabilities;
using NuExtVault.Operations;
using NuExtVault.Packages;

namespace NuExtVault.Kernel;

/// <summary>
/// Kernel dispatch for typed operations. It enforces the declared contract binding,
/// propagates cancellation, classifies owner failures into typed errors, and records
/// operation-attributed diagnostics.
/// </summary>
internal sealed class OperationDispatcher(
    OperationRegistry registry,
    ServerDiagnostics diagnostics)
{
    public OperationRegistry Registry { get; } =
        registry ?? throw new ArgumentNullException(nameof(registry));

    public async ValueTask<OperationResponse<TResponse>> DispatchAsync<TRequest, TResponse>(
        OperationId operationId,
        TRequest request,
        OperationExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operationId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        if (!Registry.TryGet(operationId, out var registration))
        {
            return Fail<TResponse>(
                operationId,
                OperationErrorPolicy.Internal(
                    $"Operation '{operationId.Value}' has no registered owner."));
        }

        if (registration!.RequestType != typeof(TRequest) ||
            registration.ResponseType != typeof(TResponse))
        {
            return Fail<TResponse>(
                operationId,
                OperationErrorPolicy.Internal(
                    $"Operation '{operationId.Value}' declares contracts " +
                    $"'{registration.RequestType.Name}'/'{registration.ResponseType.Name}' and " +
                    $"cannot be dispatched with " +
                    $"'{typeof(TRequest).Name}'/'{typeof(TResponse).Name}'."));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var activity = Activity.Current;
        activity?.SetTag("nuget.operation.id", operationId.Value);
        activity?.SetTag("nuget.operation.owner", registration.ExtensionId);
        var started = Stopwatch.GetTimestamp();
        using var attribution = CapabilityOperationAttribution.Enter(operationId.Value);
        using var execution = OperationExecutionScope.Enter(context);
        try
        {
            var result = (OperationResponse<TResponse>)await registration.Invoke(
                request,
                context,
                cancellationToken);
            Record(operationId, result.Error?.Code ?? "success", started);
            return result;
        }
        catch (OperationCanceledException)
        {
            Record(operationId, "canceled", started);
            throw;
        }
        catch (PackageLimitExceededException exception)
        {
            return Fail<TResponse>(
                operationId,
                OperationErrorPolicy.LimitExceeded(exception.Message),
                started);
        }
        catch (CapabilityQuotaExceededException exception)
        {
            return Fail<TResponse>(
                operationId,
                OperationErrorPolicy.Unavailable(exception.Message, exception.RetryAfterSeconds),
                started);
        }
        catch (InvalidPackageException exception)
        {
            return Fail<TResponse>(
                operationId,
                OperationErrorPolicy.InvalidRequest(exception.Message),
                started);
        }
        catch (DuplicatePackageException exception)
        {
            return Fail<TResponse>(
                operationId,
                OperationErrorPolicy.Conflict(exception.Message),
                started);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or SqliteException)
        {
            diagnostics.RecordStorageFailure();
            Record(operationId, "storage-failure", started);
            throw;
        }
        catch
        {
            Record(operationId, OperationErrorCodes.Internal, started);
            throw;
        }
    }

    private OperationResponse<TResponse> Fail<TResponse>(
        OperationId operationId,
        OperationError error,
        long? started = null)
    {
        Record(operationId, error.Code, started ?? Stopwatch.GetTimestamp());
        return OperationResponse<TResponse>.Failure(error);
    }

    private void Record(OperationId operationId, string outcome, long started) =>
        diagnostics.RecordOperation(
            operationId.Value,
            outcome,
            Stopwatch.GetElapsedTime(started));
}

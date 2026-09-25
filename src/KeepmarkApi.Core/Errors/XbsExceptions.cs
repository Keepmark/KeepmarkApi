namespace KeepmarkApi.Errors;

/// <summary>Base class mirroring api/src/exception.ts ApiException.
/// Code is the class name so old clients mapping status codes keep working.</summary>
public abstract class XbsException(string message, int statusCode) : Exception(message)
{
    public string Code => GetType().Name;
    public int StatusCode { get; } = statusCode;
}

public sealed class InvalidSyncIdException(string? message = null)
    : XbsException(message ?? "Invalid sync ID", 401);

public sealed class SyncNotFoundException(string? message = null)
    : XbsException(message ?? "Sync does not exist", 401);

public sealed class RequiredDataNotFoundException(string? message = null)
    : XbsException(message ?? "Unable to find required data", 400);

public sealed class NewSyncsForbiddenException(string? message = null)
    : XbsException(message ?? "The service is not accepting new syncs", 405);

public sealed class NewSyncsLimitExceededException(string? message = null)
    : XbsException(message ?? "Client has exceeded the daily new syncs limit", 406);

public sealed class RouteNotImplementedException(string? message = null)
    : XbsException(message ?? "The requested route has not been implemented", 404);

public sealed class SyncConflictException(string? message = null)
    : XbsException(message ?? "A sync conflict was detected", 409);

public sealed class UnsupportedVersionException(string? message = null)
    : XbsException(message ?? "The requested API version is not supported", 412);

public sealed class SyncDataLimitExceededException(string? message = null)
    : XbsException(message ?? "Sync data limit exceeded", 413);

public sealed class RequestThrottledException(string? message = null)
    : XbsException(message ?? "Too many requests", 429);

public sealed class OriginNotPermittedException(string? message = null)
    : XbsException(message ?? "Client not permitted to access this service", 500);

public sealed class UnspecifiedException(string? message = null)
    : XbsException(message ?? "An unspecified error has occurred", 500);

public sealed class ServiceNotAvailableException(string? message = null)
    : XbsException(message ?? "The service is currently offline", 503);

using KeepmarkApi.Errors;

namespace KeepmarkApi.Errors;

/// <summary>Pure error mapping (status, code, message) so it is unit-testable.
/// Used by the ASP.NET middleware and the rate-limiter rejection handler.</summary>
public static class ErrorMap
{
    public static (int Status, string Code, string Message) Map(Exception ex)
        => ex switch
        {
            XbsException x => (x.StatusCode, x.Code, x.Message),
            _ => (500, nameof(UnspecifiedException), new UnspecifiedException().Message),
        };
}

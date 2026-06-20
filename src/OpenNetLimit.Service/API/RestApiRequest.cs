namespace OpenNetLimit.Service.API;

public sealed record RestApiRequest(
    string Method,
    string Path,
    string QueryString,
    string Body,
    bool IsLoopback,
    string? ApiKey);

using System.Text.Json;

namespace OpenNetLimit.Service.API;

public sealed record RestApiResponse(int StatusCode, string ContentType, string Body)
{
    public static RestApiResponse Json<T>(int statusCode, T body, JsonSerializerOptions options) =>
        new(statusCode, "application/json; charset=utf-8", JsonSerializer.Serialize(body, options));

    public static RestApiResponse Error(int statusCode, string message, JsonSerializerOptions options) =>
        Json(statusCode, new { error = message }, options);
}

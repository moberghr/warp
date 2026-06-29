using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Warp.Core.Handlers;

namespace Warp.Http.Dispatch;

/// <summary>
/// <b>Generated-code support — not intended for direct use.</b> Helpers used by
/// source-generated <see cref="RequestDelegate"/> code to read JSON request bodies
/// and write <see cref="IRequest{TResponse}"/> results. Public only because
/// generated code in consumer assemblies must call into them; treat as
/// implementation detail.
/// </summary>
public static class JsonResponseWriter
{
    /// <summary>Writes <paramref name="value"/> as JSON with status 200.</summary>
    public static async Task WriteAsync<TResponse>(HttpContext context, TResponse value, CancellationToken cancellationToken)
    {
        // A handler that returns an IResult (Results.Stream / File / NotFound / Ok, etc.) owns
        // the entire response — binary payloads, custom content types, and bodyless status codes
        // don't fit the JSON envelope. Let the result write itself and skip JSON serialization.
        if (value is IResult result)
        {
            await result.ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        if (typeof(TResponse) == typeof(Unit))
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        var jsonOptions = context.RequestServices.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value;

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";

        // IHttpResponseShape lets the response type customize status/headers/Location
        // before the body is written. Runs only for non-Unit responses.
        if (value is IHttpResponseShape shape)
        {
            shape.Apply(context);
        }

        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            value,
            jsonOptions.SerializerOptions,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads <typeparamref name="TRequest"/> from the JSON request body.</summary>
    public static async ValueTask<TRequest> ReadBodyAsync<TRequest>(HttpContext context, CancellationToken cancellationToken)
    {
        var jsonOptions = context.RequestServices.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value;

        var value = await JsonSerializer.DeserializeAsync<TRequest>(
            context.Request.Body,
            jsonOptions.SerializerOptions,
            cancellationToken).ConfigureAwait(false) ?? throw new BadHttpRequestException("Request body could not be deserialized to " + typeof(TRequest).Name + ".");
        return value;
    }
}

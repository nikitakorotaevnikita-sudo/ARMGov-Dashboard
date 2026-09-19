#nullable enable

using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ArmGov.Harness.Hosting;

public sealed class AnalysisEndpoint
{
    public const int MaxBodyBytes = 64 * 1024;

    private readonly Func<AnalysisRequest, CancellationToken, Task<AnalysisResponse>> _runAsync;
    private readonly Func<bool> _isEnabled;

    public AnalysisEndpoint(
        Func<AnalysisRequest, CancellationToken, Task<AnalysisResponse>> runAsync,
        Func<bool>? isEnabled = null)
    {
        _runAsync = runAsync ?? throw new ArgumentNullException(nameof(runAsync));
        _isEnabled = isEnabled ?? (() => true);
    }

    public Task HandleAsync(HttpListenerContext ctx, CancellationToken ct) =>
        HandleRequestAsync(
            ctx.Request.HttpMethod,
            ctx.Request.IsLocal,
            ctx.Request.RemoteEndPoint?.Address ?? IPAddress.None,
            ctx.Request.UserHostName,
            ctx.Request.Headers["Origin"],
            ctx.Request.ContentType,
            ctx.Request.ContentLength64,
            ctx.Request.InputStream,
            async (statusCode, payload) =>
            {
                await WriteJsonAsync(ctx, statusCode, payload, ct).ConfigureAwait(false);
            },
            ct);

    public async Task HandleRequestAsync(
        string method,
        bool isLocal,
        IPAddress remoteAddress,
        string? hostName,
        string? origin,
        string? contentType,
        long contentLength,
        Stream? bodyStream,
        Func<int, object, Task> writeAsync,
        CancellationToken ct)
    {
        if (!string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            await writeAsync(405, new { error = "только POST" }).ConfigureAwait(false);
            return;
        }

        if (!IsLocalRequest(isLocal, remoteAddress, hostName))
        {
            await writeAsync(403, new { error = "харнесс доступен только при локальном вызове" })
                .ConfigureAwait(false);
            return;
        }

        if (!_isEnabled())
        {
            await writeAsync(404, new { error = "аналитика отключена" }).ConfigureAwait(false);
            return;
        }

        if (!LocalAccess.IsSameOrigin(origin, hostName))
        {
            await writeAsync(403, new { error = "запрос отклонён: origin не совпадает с host" })
                .ConfigureAwait(false);
            return;
        }

        var normalizedType = contentType ?? "";
        if (!normalizedType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
        {
            await writeAsync(400, new { error = "Content-Type должен быть application/json" })
                .ConfigureAwait(false);
            return;
        }

        if (contentLength <= 0)
        {
            await writeAsync(400, new { error = "тело запроса обязательно" }).ConfigureAwait(false);
            return;
        }

        if (contentLength > MaxBodyBytes)
        {
            await writeAsync(413, new { error = "тело запроса превышает 64 KiB" }).ConfigureAwait(false);
            return;
        }

        string body;
        try
        {
            using var reader = new StreamReader(bodyStream ?? Stream.Null, Encoding.UTF8);
            body = await reader.ReadToEndAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await writeAsync(400, new { error = Sanitize(ex.Message) }).ConfigureAwait(false);
            return;
        }

        if (body.Length > MaxBodyBytes)
        {
            await writeAsync(413, new { error = "тело запроса превышает 64 KiB" }).ConfigureAwait(false);
            return;
        }

        AnalysisRequest request;
        try
        {
            request = JsonSerializer.Deserialize<AnalysisRequest>(body, HarnessJson.Options)
                ?? throw new JsonException("empty body");
        }
        catch (JsonException)
        {
            await writeAsync(400, new { error = "тело должно быть JSON-объектом AnalysisRequest" })
                .ConfigureAwait(false);
            return;
        }

        var validation = AnalysisRequestValidator.Validate(request);
        if (!validation.Ok)
        {
            await writeAsync(400, new
            {
                error = validation.Errors[0].Message,
                code = validation.Errors[0].Code
            }).ConfigureAwait(false);
            return;
        }

        try
        {
            var response = await _runAsync(request, ct).ConfigureAwait(false);
            await writeAsync(200, response).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HarnessException ex)
        {
            await writeAsync(200, new AnalysisResponse(
                Guid.NewGuid().ToString("N"),
                "failed",
                null,
                Array.Empty<StoredResult>(),
                Array.Empty<AgentStep>(),
                0,
                Array.Empty<string>(),
                null,
                ex.Error)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await writeAsync(500, new { error = Sanitize(ex.Message) }).ConfigureAwait(false);
        }
    }

    public static bool IsLocalRequest(HttpListenerContext ctx) =>
        IsLocalRequest(
            ctx.Request.IsLocal,
            ctx.Request.RemoteEndPoint.Address,
            ctx.Request.UserHostName);

    public static bool IsLocalRequest(bool isLocal, IPAddress remoteAddress, string? hostName)
    {
        if (!isLocal && !IPAddress.IsLoopback(remoteAddress))
            return false;
        return LocalAccess.IsLocalHostName(hostName);
    }

    private static async Task WriteJsonAsync(
        HttpListenerContext ctx,
        int statusCode,
        object payload,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var json = JsonSerializer.Serialize(payload, HarnessJson.Options);
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.StatusCode = statusCode;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.AddHeader("Cache-Control", "no-store");
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
        ctx.Response.OutputStream.Close();
    }

    private static string Sanitize(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "ошибка обработки запроса";

        var text = message.Split('\n', '\r')[0];
        if (text.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0 ||
            text.IndexOf("token", StringComparison.OrdinalIgnoreCase) >= 0 ||
            text.IndexOf("Host=", StringComparison.OrdinalIgnoreCase) >= 0)
            return "ошибка обработки запроса";

        return text.Length > 300 ? text[..300] : text;
    }
}

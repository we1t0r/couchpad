using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace RemoteDesk.Agent.Core;

/// <summary>
/// HTTPS-часть протокола для файлов (см. «Файлы» в protocol/PROTOCOL.md). Тот же сертификат, что и у WebSocket;
/// телефон представляется заголовками X-RDesk-Device и X-RDesk-Token.
/// </summary>
static class FileApi
{
    public static async Task HandleAsync(AgentServer server, HttpContext ctx)
    {
        var device = server.Devices.Validate(ctx.Request.Headers["X-RDesk-Device"].ToString(), ctx.Request.Headers["X-RDesk-Token"].ToString());
        if (device is null)
        {
            await Fail(ctx, 401, Protocol.Errors.Revoked, "Этот телефон не сопряжён с компьютером");
            return;
        }
        if (!Protocol.DefaultPermissions.Contains("files"))
        {
            await Fail(ctx, 403, Protocol.Errors.Forbidden, "Доступ к файлам выключен на компьютере");
            return;
        }

        var files = server.Services.Files;
        var q = ctx.Request.Query;
        string? root = q["root"], path = q["path"], name = q["name"];
        var op = ctx.Request.RouteValues["op"] as string;
        var method = ctx.Request.Method;
        try
        {
            switch (op)
            {
                case "roots" when method == "GET":
                    await Json(ctx, new { roots = files.Roots().Select(r => new { id = r.Id, name = r.Name, writable = r.Writable }) });
                    break;
                case "list" when method == "GET":
                    await Json(ctx, new
                    {
                        entries = files.List(root!, path).Select(e => new
                        {
                            name = e.Name,
                            dir = e.Dir,
                            size = e.Size,
                            modified = e.Modified.ToUnixTimeMilliseconds(),
                        }),
                    });
                    break;
                case "download" when method is "GET" or "HEAD":
                    var full = files.ReadablePath(root!, path);
                    // Range-запросы — докачка с места обрыва.
                    await Results.File(full, "application/octet-stream", Path.GetFileName(full), enableRangeProcessing: true).ExecuteAsync(ctx);
                    break;
                case "mkdir" when method == "POST":
                    files.CreateFolder(root!, path);
                    await Json(ctx, new { ok = true });
                    break;
                case "rename" when method == "POST":
                    files.Rename(root!, path, name);
                    await Json(ctx, new { ok = true });
                    break;
                case "delete" when method == "POST":
                    files.Delete(root!, path, server.Services.Recycle);
                    await Json(ctx, new { ok = true });
                    break;
                case "open" when method == "POST":
                    files.OpenOnPc(root!, path);
                    await Json(ctx, new { ok = true });
                    break;
                case "upload/begin" when method == "POST":
                    var conflict = q["conflict"].ToString() switch
                    {
                        "replace" => FileService.Conflict.Replace,
                        "rename" => FileService.Conflict.Rename,
                        _ => FileService.Conflict.Ask,
                    };
                    var (finalName, have) = files.BeginUpload(root!, path, name, conflict);
                    await Json(ctx, new { name = finalName, have });
                    break;
                case "upload" when method == "PUT":
                    // Файлы бывают большими: снимаем ограничение Kestrel на размер тела (30 МБ по умолчанию).
                    if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                        limit.MaxRequestBodySize = null;
                    if (!long.TryParse(q["size"], out var size) || !long.TryParse(q["offset"], out var offset) || size < 0 || offset < 0)
                    {
                        await Fail(ctx, 400, Protocol.Errors.BadMessage, "Нужны size и offset");
                        break;
                    }
                    var received = await files.UploadAsync(root!, path, name, size, offset, ctx.Request.Body, ctx.RequestAborted);
                    await Json(ctx, new { have = received, done = received == size });
                    break;
                default:
                    await Fail(ctx, 404, Protocol.Errors.BadMessage, "Неизвестная операция");
                    break;
            }
        }
        catch (FileAccessException e)
        {
            await Fail(ctx, e.Status, e.Code, e.Message);
        }
        catch (UnauthorizedAccessException)
        {
            await Fail(ctx, 403, "denied", "Windows не даёт доступ к этому файлу");
        }
        catch (IOException e) when (!ctx.RequestAborted.IsCancellationRequested)
        {
            await Fail(ctx, 500, "io", e.Message);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or BadHttpRequestException)
        {
            // Телефон оборвал передачу — докачается позже.
        }
    }

    static Task Json(HttpContext ctx, object body) => ctx.Response.WriteAsJsonAsync(body, Message.Options);

    static async Task Fail(HttpContext ctx, int status, string code, string msg)
    {
        if (ctx.Response.HasStarted)
            return;
        ctx.Response.StatusCode = status;
        await Json(ctx, new { code, msg });
    }
}

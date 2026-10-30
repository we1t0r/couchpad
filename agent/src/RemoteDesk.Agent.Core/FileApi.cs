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
                    // Отправленное с ПК («Отправить на телефон») лежит где угодно — его выдаём по id.
                    var full = root == Outbox.Root
                        ? server.Services.Outbox.PathOf(path) ?? throw new FileAccessException(404, "not_found", "Файл больше не доступен — отправьте его с компьютера ещё раз")
                        : files.ReadablePath(root!, path);
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

    /// <summary>Наибольшая картинка, которую телефон может положить в буфер ПК.</summary>
    const long MaxClipImage = 50 << 20;

    /// <summary>
    /// Картинка из буфера обмена: GET — забрать с ПК (PNG), PUT — положить на ПК (PNG, JPEG и т. п.).
    /// Через HTTPS, а не WebSocket: скриншот легко больше мегабайта.
    /// </summary>
    public static async Task HandleClipImageAsync(AgentServer server, HttpContext ctx)
    {
        var device = server.Devices.Validate(ctx.Request.Headers["X-RDesk-Device"].ToString(), ctx.Request.Headers["X-RDesk-Token"].ToString());
        if (device is null)
        {
            await Fail(ctx, 401, Protocol.Errors.Revoked, "Этот телефон не сопряжён с компьютером");
            return;
        }
        if (!Protocol.DefaultPermissions.Contains("clipboard"))
        {
            await Fail(ctx, 403, Protocol.Errors.Forbidden, "Буфер обмена выключен на компьютере");
            return;
        }

        var clipboard = server.Services.Clipboard;
        try
        {
            switch (ctx.Request.Method)
            {
                case "GET":
                    if (await clipboard.GetImageAsync() is not { } image)
                    {
                        await Fail(ctx, 404, "no_image", "В буфере обмена компьютера нет картинки");
                        break;
                    }
                    ctx.Response.ContentType = "image/png";
                    ctx.Response.ContentLength = image.Length;
                    await ctx.Response.Body.WriteAsync(image, ctx.RequestAborted);
                    break;
                case "PUT":
                    if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                        limit.MaxRequestBodySize = MaxClipImage;
                    using (var body = new MemoryStream())
                    {
                        await ctx.Request.Body.CopyToAsync(body, ctx.RequestAborted);
                        if (body.Length == 0)
                        {
                            await Fail(ctx, 400, Protocol.Errors.BadMessage, "Пустая картинка");
                            break;
                        }
                        await clipboard.SetImageAsync(body.ToArray());
                    }
                    await Json(ctx, new { ok = true });
                    break;
                default:
                    await Fail(ctx, 405, Protocol.Errors.BadMessage, "Только GET или PUT");
                    break;
            }
        }
        catch (ArgumentException)
        {
            // Image.FromStream не узнал формат.
            await Fail(ctx, 400, "bad_image", "Компьютер не смог прочитать эту картинку");
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or BadHttpRequestException)
        {
            // Телефон оборвал передачу или картинка слишком большая.
            await Fail(ctx, 413, "too_big", "Картинка слишком большая или передача оборвалась");
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

using RemoteDesk.Agent.Core;

namespace RemoteDesk.Agent;

/// <summary>
/// Буфер обмена Windows. Он работает только из STA-потока, а запросы приходят из сети,
/// поэтому каждое обращение — в коротком отдельном STA-потоке.
/// </summary>
sealed class WinFormsClipboard : IClipboard
{
    public Task<string?> GetTextAsync() => OnSta(() => Clipboard.ContainsText() ? Clipboard.GetText() : null);

    public Task SetTextAsync(string text) => OnSta<object?>(() =>
    {
        if (string.IsNullOrEmpty(text))
            Clipboard.Clear();
        else
            // Несколько попыток: буфер может быть на миг занят другой программой.
            Clipboard.SetDataObject(text, copy: true, retryTimes: 5, retryDelay: 100);
        return null;
    });

    /// <summary>Браузеры и редакторы кладут в буфер PNG — он сохраняет прозрачность; иначе берём обычный рисунок.</summary>
    public Task<byte[]?> GetImageAsync() => OnSta<byte[]?>(() =>
    {
        var data = Clipboard.GetDataObject();
        if (data is null)
            return null;
#pragma warning disable WFDEV005 // поток PNG — не сериализованный объект, BinaryFormatter тут не участвует
        if (data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream png)
            return png.ToArray();
#pragma warning restore WFDEV005
        if (!Clipboard.ContainsImage() || Clipboard.GetImage() is not { } image)
            return null;
        using (image)
        using (var stream = new MemoryStream())
        {
            image.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            return stream.ToArray();
        }
    });

    /// <summary>Кладём и рисунок (его понимают все программы), и PNG (с прозрачностью — для браузеров и редакторов).</summary>
    public Task SetImageAsync(byte[] bytes) => OnSta<object?>(() =>
    {
        using var source = new MemoryStream(bytes);
        using var decoded = Image.FromStream(source);
        var bitmap = new Bitmap(decoded);
        using var png = new MemoryStream();
        bitmap.Save(png, System.Drawing.Imaging.ImageFormat.Png);
        var data = new DataObject();
        data.SetImage(bitmap);
        data.SetData("PNG", false, new MemoryStream(png.ToArray()));
        Clipboard.SetDataObject(data, copy: true, retryTimes: 5, retryDelay: 100);
        return null;
    });

    static Task<T> OnSta<T>(Func<T> work)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                result.SetResult(work());
            }
            catch (Exception e)
            {
                result.SetException(e);
            }
        }) { IsBackground = true, Name = "RemoteDesk clipboard" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return result.Task;
    }
}

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

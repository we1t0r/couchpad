using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RemoteDesk.Agent.Core;

/// <summary>Буфер обмена ПК. Реализация на WinForms живёт в приложении агента (нужен STA-поток).</summary>
public interface IClipboard
{
    Task<string?> GetTextAsync();
    Task SetTextAsync(string text);
}

public sealed class NullClipboard : IClipboard
{
    public Task<string?> GetTextAsync() => Task.FromResult<string?>(null);
    public Task SetTextAsync(string text) => Task.CompletedTask;
}

/// <summary>Удаление в корзину Windows — без окон подтверждения на экране ПК.</summary>
[SupportedOSPlatform("windows")]
public static class RecycleBin
{
    const uint FoDelete = 3;
    const ushort FofSilent = 0x4, FofNoConfirmation = 0x10, FofAllowUndo = 0x40, FofNoErrorUi = 0x400;

    public static void Send(string path)
    {
        var op = new ShFileOp
        {
            wFunc = FoDelete,
            pFrom = path + "\0", // список путей заканчивается двумя нулями; второй добавит маршалинг
            fFlags = FofSilent | FofNoConfirmation | FofAllowUndo | FofNoErrorUi,
        };
        var result = SHFileOperationW(ref op);
        if (result != 0 || op.fAnyOperationsAborted != 0)
            throw new FileAccessException(500, "delete_failed", $"Windows не дала удалить (код {result}). Возможно, файл открыт в программе.");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ShFileOp
    {
        public nint hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public nint hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHFileOperationW(ref ShFileOp op);
}

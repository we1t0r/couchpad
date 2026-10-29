using System.Text.Json.Nodes;

namespace RemoteDesk.Agent.Core;

public enum MouseButton { Left, Right, Middle }

/// <summary>Куда уходит ввод. На Windows — SendInput, в тестах — запись вызовов.</summary>
public interface IInputSink
{
    void Move(int dx, int dy);
    void Button(MouseButton button, bool down);

    /// <summary>Прокрутка в единицах WHEEL_DELTA/120: 120 — один щелчок колеса.</summary>
    void Wheel(int vertical, int horizontal);

    void Key(ushort vk, bool down);
    void Text(string text);
}

/// <summary>Ничего не делает — для систем без поддержки ввода.</summary>
public sealed class NullInput : IInputSink
{
    public void Move(int dx, int dy) { }
    public void Button(MouseButton button, bool down) { }
    public void Wheel(int vertical, int horizontal) { }
    public void Key(ushort vk, bool down) { }
    public void Text(string text) { }
}

/// <summary>
/// Записывает ввод строками вместо настоящих мыши и клавиатуры: для тестов и режима разработки
/// (<c>--input-log файл</c>), чтобы автотест не дёргал курсор на ПК разработчика.
/// </summary>
public sealed class RecordingInput(string? logFile = null) : IInputSink
{
    readonly List<string> lines = [];

    public IReadOnlyList<string> Lines
    {
        get { lock (lines) return [.. lines]; }
    }

    public void Move(int dx, int dy) => Add($"move {dx} {dy}");
    public void Button(MouseButton button, bool down) => Add($"button {button.ToString().ToLowerInvariant()} {(down ? "down" : "up")}");
    public void Wheel(int vertical, int horizontal) => Add($"wheel {vertical} {horizontal}");
    public void Key(ushort vk, bool down) => Add($"key 0x{vk:X2} {(down ? "down" : "up")}");
    public void Text(string text) => Add($"text {text}");

    void Add(string line)
    {
        lock (lines)
        {
            lines.Add(line);
            if (logFile is not null)
                File.AppendAllText(logFile, line + "\n");
        }
    }
}

/// <summary>
/// Ввод одного телефона: разбирает сообщения, копит дробные остатки движения и прокрутки
/// и помнит, что нажато, чтобы отпустить всё при обрыве связи.
/// </summary>
internal sealed class InputHandler(IInputSink sink)
{
    const int MaxStep = 4000;
    const int MaxText = 4096;

    readonly HashSet<ushort> heldKeys = [];
    readonly HashSet<MouseButton> heldButtons = [];
    double restX, restY, restWheelY, restWheelX;

    public static bool Handles(string? type) => type is "move" or "button" or "click" or "scroll" or "key" or "keys" or "text";

    /// <summary>null — выполнено; иначе текст ошибки для bad_message.</summary>
    public string? Handle(JsonObject msg)
    {
        switch (msg.Str("t"))
        {
            case "move":
            {
                var (dx, dy) = Take(ref restX, msg.Dbl("dx") ?? 0, ref restY, msg.Dbl("dy") ?? 0, 1);
                if (dx != 0 || dy != 0)
                    sink.Move(Math.Clamp(dx, -MaxStep, MaxStep), Math.Clamp(dy, -MaxStep, MaxStep));
                return null;
            }
            case "scroll":
            {
                var (h, v) = Take(ref restWheelX, msg.Dbl("dx") ?? 0, ref restWheelY, msg.Dbl("dy") ?? 0, 120);
                if (h != 0 || v != 0)
                    sink.Wheel(Math.Clamp(v, -120 * 50, 120 * 50), Math.Clamp(h, -120 * 50, 120 * 50));
                return null;
            }
            case "button":
            {
                if (ParseButton(msg.Str("b")) is not { } b)
                    return "Неизвестная кнопка мыши";
                var down = msg.Bool("down") ?? false;
                if (down ? heldButtons.Add(b) : heldButtons.Remove(b))
                    sink.Button(b, down);
                return null;
            }
            case "click":
            {
                if (ParseButton(msg.Str("b") ?? "left") is not { } b)
                    return "Неизвестная кнопка мыши";
                // Если кнопка уже зажата (перетаскивание), сначала отпускаем её.
                if (heldButtons.Remove(b))
                    sink.Button(b, false);
                var n = Math.Clamp(msg.Num("n") ?? 1, 1, 3);
                for (var i = 0; i < n; i++)
                {
                    sink.Button(b, true);
                    sink.Button(b, false);
                }
                return null;
            }
            case "key":
            {
                if (Keys.Parse(msg.Str("k")) is not { } vk)
                    return $"Неизвестная клавиша «{msg.Str("k")}»";
                var down = msg.Bool("down") ?? false;
                if (down)
                {
                    heldKeys.Add(vk);
                    sink.Key(vk, true);
                }
                else if (heldKeys.Remove(vk))
                {
                    sink.Key(vk, false);
                }
                return null;
            }
            case "keys":
            {
                if (msg["k"] is not JsonArray array || array.Count is 0 or > 8)
                    return "Нужен массив клавиш (до 8)";
                var codes = new List<ushort>();
                foreach (var item in array)
                {
                    var name = item is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
                    if (Keys.Parse(name) is not { } vk)
                        return $"Неизвестная клавиша «{name}»";
                    codes.Add(vk);
                }
                foreach (var vk in codes)
                    sink.Key(vk, true);
                for (var i = codes.Count - 1; i >= 0; i--)
                {
                    // Клавишу, которую телефон держит отдельно (например, Alt для Alt+Tab), не отпускаем.
                    if (!heldKeys.Contains(codes[i]))
                        sink.Key(codes[i], false);
                }
                return null;
            }
            case "text":
            {
                var text = msg.Str("s");
                if (string.IsNullOrEmpty(text))
                    return null;
                if (text.Length > MaxText)
                    return $"Слишком длинный текст (больше {MaxText} символов)";
                sink.Text(text);
                return null;
            }
            default:
                return "Не ввод";
        }
    }

    /// <summary>Отпускает всё, что телефон держал нажатым. Вызывается при закрытии соединения.</summary>
    public void ReleaseAll()
    {
        foreach (var b in heldButtons)
            sink.Button(b, false);
        heldButtons.Clear();
        foreach (var vk in heldKeys)
            sink.Key(vk, false);
        heldKeys.Clear();
    }

    static (int, int) Take(ref double restA, double a, ref double restB, double b, double scale)
    {
        restA += a * scale;
        restB += b * scale;
        var ia = (int)Math.Truncate(Math.Clamp(restA, -1e6, 1e6));
        var ib = (int)Math.Truncate(Math.Clamp(restB, -1e6, 1e6));
        restA -= ia;
        restB -= ib;
        return (ia, ib);
    }

    static MouseButton? ParseButton(string? name) => name switch
    {
        "left" => MouseButton.Left,
        "right" => MouseButton.Right,
        "middle" => MouseButton.Middle,
        _ => null,
    };
}

/// <summary>Имена клавиш протокола → виртуальные коды Windows.</summary>
public static class Keys
{
    static readonly Dictionary<string, ushort> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["backspace"] = 0x08, ["tab"] = 0x09, ["enter"] = 0x0D, ["shift"] = 0x10, ["ctrl"] = 0x11, ["alt"] = 0x12,
        ["pause"] = 0x13, ["capslock"] = 0x14, ["esc"] = 0x1B, ["space"] = 0x20, ["pageup"] = 0x21, ["pagedown"] = 0x22,
        ["end"] = 0x23, ["home"] = 0x24, ["left"] = 0x25, ["up"] = 0x26, ["right"] = 0x27, ["down"] = 0x28,
        ["printscreen"] = 0x2C, ["insert"] = 0x2D, ["delete"] = 0x2E, ["win"] = 0x5B, ["menu"] = 0x5D,
        ["numlock"] = 0x90, ["scrolllock"] = 0x91,
        ["volumemute"] = 0xAD, ["volumedown"] = 0xAE, ["volumeup"] = 0xAF,
        ["nexttrack"] = 0xB0, ["prevtrack"] = 0xB1, ["stop"] = 0xB2, ["playpause"] = 0xB3,
    };

    /// <summary>Клавиши, которым в SendInput нужен флаг KEYEVENTF_EXTENDEDKEY.</summary>
    public static bool IsExtended(ushort vk) => vk is
        (>= 0x21 and <= 0x28) // PgUp, PgDn, End, Home, стрелки
        or 0x2C or 0x2D or 0x2E // PrintScreen, Insert, Delete
        or 0x5B or 0x5C or 0x5D // Win, Menu
        or 0x6F or 0x90 // Num /, NumLock
        or 0xA3 or 0xA5 // правые Ctrl и Alt
        or (>= 0xA6 and <= 0xB7); // браузерные и медиаклавиши

    public static ushort? Parse(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return null;
        if (Named.TryGetValue(name, out var vk))
            return vk;
        if (name.Length == 1 && char.IsAsciiLetterOrDigit(name[0]))
            return char.ToUpperInvariant(name[0]);
        if (name.Length is 2 or 3 && name[0] is 'f' or 'F' && int.TryParse(name.AsSpan(1), out var f) && f is >= 1 and <= 24)
            return (ushort)(0x70 + f - 1);
        if (name.StartsWith("vk:0x", StringComparison.OrdinalIgnoreCase)
            && ushort.TryParse(name.AsSpan(5), System.Globalization.NumberStyles.HexNumber, null, out var raw)
            && raw is > 0 and < 0xFF)
            return raw;
        return null;
    }
}

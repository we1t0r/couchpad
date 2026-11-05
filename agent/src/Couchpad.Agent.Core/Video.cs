namespace Couchpad.Agent.Core;

/// <summary>Кадр видео H.264: единица доступа в виде Annex B (00 00 00 01 …). Ключевой кадр несёт SPS и PPS.</summary>
public sealed record VideoFrame(byte[] Data, bool Key);

/// <summary>Курсор: где он (доли ширины и высоты монитора) и виден ли. В видео его нет — телефон рисует сам.</summary>
public readonly record struct CursorPosition(double X, double Y, bool Visible);

/// <summary>Картинка курсора: PNG, горячая точка и размер в пикселях монитора.</summary>
public sealed record CursorShape(byte[] Png, int HotX, int HotY, int Width, int Height);

/// <summary>Видео с монитора через видеокарту: захват и сжатие в H.264.</summary>
public interface IVideoCapture : IDisposable
{
    /// <summary>Размер кадров видео (может быть меньше монитора).</summary>
    int Width { get; }

    int Height { get; }

    /// <summary>
    /// Следующий кадр. Ждёт изменений на экране до <paramref name="timeoutMs"/>; null — ничего не поменялось.
    /// Исключение — видео сейчас невозможно (экран блокировки, окно UAC, смена режима): поток перейдёт на JPEG.
    /// </summary>
    VideoFrame? Next(int timeoutMs);

    /// <summary>Следующий кадр — ключевой (телефон заново открыл декодер).</summary>
    void RequestKeyFrame();

    /// <summary>Курсор сейчас (обновляется в <see cref="Next"/>).</summary>
    CursorPosition Cursor { get; }

    /// <summary>Картинка курсора; новый объект — значит, курсор сменился.</summary>
    CursorShape? Shape { get; }
}

/// <summary>Источник экрана, который умеет и видео. Без него — только JPEG.</summary>
public interface IVideoSource
{
    /// <summary>Видео монитора: ширина не больше <paramref name="maxWidth"/>. Исключение — видео недоступно.</summary>
    IVideoCapture OpenVideo(MonitorInfo monitor, int maxWidth, int fps, int bitrate);
}

/// <summary>Видео сейчас невозможно (например, компьютер заблокирован) — показываем JPEG.</summary>
public sealed class VideoUnavailableException(string message) : Exception(message);

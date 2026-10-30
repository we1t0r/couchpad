using System.IO.Pipes;
using RemoteDesk.Agent.Core;

namespace RemoteDesk.Agent.Tests;

/// <summary>Канал агента пользователя к помощнику службы входа — на ненастоящих вводе и экране.</summary>
public sealed class BridgeTests
{
    readonly string pipeName = "Couchpad.Test." + Guid.NewGuid().ToString("N");
    readonly RecordingInput helperInput = new();
    bool allowed = true;

    async Task<(DesktopBridgeClient Client, CancellationTokenSource Stop)> StartAsync()
    {
        var stop = new CancellationTokenSource();
        var server = new DesktopBridgeServer(helperInput, new SyntheticScreen(), () => allowed,
            () => new NamedPipeServerStream(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous));
        _ = server.RunAsync(stop.Token);
        await Task.Delay(100);
        return (new DesktopBridgeClient(() => new NamedPipeClientStream(".", pipeName, PipeDirection.InOut)), stop);
    }

    [Fact]
    public async Task Input_and_frames_go_through_the_helper_only_while_allowed()
    {
        var (client, stop) = await StartAsync();
        using (client)
        using (stop)
        {
            client.MoveTo(10, 20);
            client.Button(MouseButton.Left, true);
            client.Text("1234\n");
            Assert.Equal(2, client.Monitors().Count);
            using var capture = client.Open("test-2");
            Assert.Equal("test-2", capture.Monitor.Id);
            var frame = capture.Capture(800, 70, force: true);
            Assert.Equal((16, 9), (frame!.Width, frame.Height));
            Assert.Null(capture.Capture(800, 70, force: false));
            Assert.Equal(["moveto 10 20", "button left down", "text 1234\n"], helperInput.Lines);

            // Компьютер разблокирован — помощник не нажимает и не показывает.
            allowed = false;
            client.Key(0x41, true);
            Assert.Throws<InvalidOperationException>(() => capture.Capture(800, 70, force: true));
            Assert.Equal(3, helperInput.Lines.Count);
        }
    }

    [Fact]
    public void Without_helper_input_is_dropped_and_there_are_no_frames()
    {
        using var client = new DesktopBridgeClient(() => new NamedPipeClientStream(".", pipeName, PipeDirection.InOut));
        client.Move(1, 1);
        Assert.False(client.Available);
        Assert.Empty(client.Monitors());
        Assert.Throws<InvalidOperationException>(() => client.Open(null));
    }

    [Fact]
    public async Task Untrusted_pipe_owner_is_not_used()
    {
        var (_, stop) = await StartAsync();
        using (stop)
        using (var client = new DesktopBridgeClient(() => new NamedPipeClientStream(".", pipeName, PipeDirection.InOut), _ => false))
        {
            client.Text("PIN");
            Assert.Empty(client.Monitors());
            Assert.Empty(helperInput.Lines);
        }
    }

    [Fact]
    public void Lock_aware_desktop_switches_to_the_lock_screen_and_back()
    {
        var own = new RecordingInput();
        var lockInput = new RecordingInput();
        var locked = false;
        var desktop = new LockAwareDesktop(own, new SyntheticScreen(), lockInput, new SyntheticScreen(), () => locked);
        using var capture = desktop.Open("test-1");
        Assert.NotNull(capture.Capture(800, 70, force: true));
        desktop.Text("a");

        locked = true;
        // Сменился экран — кадр сразу, хоть «ничего не изменилось».
        Assert.NotNull(capture.Capture(800, 70, force: false));
        desktop.Text("1234");

        locked = false;
        Assert.NotNull(capture.Capture(800, 70, force: false));
        Assert.Equal(["text a"], own.Lines);
        Assert.Equal(["text 1234"], lockInput.Lines);
    }
}

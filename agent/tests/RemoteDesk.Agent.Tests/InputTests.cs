using RemoteDesk.Agent.Core;

namespace RemoteDesk.Agent.Tests;

public sealed class InputTests
{
    readonly RecordingInput sink = new();
    readonly InputHandler input;

    public InputTests() => input = new InputHandler(sink);

    string? Send(string type, params (string, object?)[] fields) => input.Handle(Message.Parse(Message.Build(type, fields))!);

    [Fact]
    public void Fractional_moves_accumulate()
    {
        Send("move", ("dx", 0.4), ("dy", -0.6));
        Send("move", ("dx", 0.4), ("dy", -0.6));
        Send("move", ("dx", 0.4), ("dy", 0.2));

        // 0.4 → 0; 0.8 → 0; 1.2 → 1. По Y: −0.6 → 0; −1.2 → −1 (остаток −0.2); −0.2+0.2 → 0.
        Assert.Equal(["move 0 -1", "move 1 0"], sink.Lines);
    }

    [Fact]
    public void Scroll_converts_to_wheel_units()
    {
        Send("scroll", ("dy", 1.0));
        Send("scroll", ("dy", -0.25), ("dx", 0.5));
        Assert.Equal(["wheel 120 0", "wheel -30 60"], sink.Lines);
    }

    [Fact]
    public void Click_and_double_click()
    {
        Send("click", ("b", "right"));
        Send("click", ("b", "left"), ("n", 2));
        Assert.Equal(
            ["button right down", "button right up", "button left down", "button left up", "button left down", "button left up"],
            sink.Lines);
    }

    [Fact]
    public void Combo_presses_in_order_and_releases_in_reverse()
    {
        Assert.Null(Send("keys", ("k", new[] { "ctrl", "shift", "esc" })));
        Assert.Equal(["key 0x11 down", "key 0x10 down", "key 0x1B down", "key 0x1B up", "key 0x10 up", "key 0x11 up"], sink.Lines);
    }

    [Fact]
    public void Held_alt_survives_alt_tab_combo_until_released()
    {
        Send("key", ("k", "alt"), ("down", true));
        Send("keys", ("k", new[] { "alt", "tab" }));
        Send("keys", ("k", new[] { "alt", "tab" }));
        Assert.DoesNotContain("key 0x12 up", sink.Lines);

        Send("key", ("k", "alt"), ("down", false));
        Assert.Equal("key 0x12 up", sink.Lines[^1]);
        input.ReleaseAll();
        Assert.Single(sink.Lines, l => l == "key 0x12 up");
    }

    [Fact]
    public void Release_without_press_does_nothing()
    {
        Send("button", ("b", "left"), ("down", false));
        Send("key", ("k", "shift"), ("down", false));
        Assert.Empty(sink.Lines);
    }

    [Fact]
    public void Text_is_passed_as_is()
    {
        Send("text", ("s", "Привет 😀\n"));
        Assert.Equal(["text Привет 😀\n"], sink.Lines);
    }

    [Theory]
    [InlineData("a", 0x41)]
    [InlineData("Z", 0x5A)]
    [InlineData("7", 0x37)]
    [InlineData("f1", 0x70)]
    [InlineData("F12", 0x7B)]
    [InlineData("f24", 0x87)]
    [InlineData("enter", 0x0D)]
    [InlineData("win", 0x5B)]
    [InlineData("playpause", 0xB3)]
    [InlineData("vk:0x2C", 0x2C)]
    public void Key_names(string name, int vk) => Assert.Equal((ushort)vk, Keys.Parse(name));

    [Theory]
    [InlineData("")]
    [InlineData("f25")]
    [InlineData("ctrl+c")]
    [InlineData("vk:0x00")]
    [InlineData("я")]
    public void Unknown_keys(string name) => Assert.Null(Keys.Parse(name));

    [Fact]
    public void Bad_messages_are_reported()
    {
        Assert.NotNull(Send("button", ("b", "fourth"), ("down", true)));
        Assert.NotNull(Send("keys", ("k", Array.Empty<string>())));
        Assert.NotNull(Send("text", ("s", new string('x', 5000))));
        Assert.Empty(sink.Lines);
    }
}

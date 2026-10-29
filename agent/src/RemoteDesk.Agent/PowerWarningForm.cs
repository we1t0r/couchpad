using RemoteDesk.Agent.Core;

namespace RemoteDesk.Agent;

/// <summary>Окно поверх всех: «Компьютер выключится через 0:59» с кнопкой «Отменить».</summary>
sealed class PowerWarningForm : Form
{
    readonly PowerTimer timer;
    readonly Label countdown = new() { AutoSize = true, Font = new Font("Segoe UI Semibold", 14f) };
    readonly System.Windows.Forms.Timer tick = new() { Interval = 500 };

    public PowerWarningForm(PowerTimer timer, Action cancel)
    {
        this.timer = timer;
        Text = "RemoteDesk";
        Icon = AppIcon.Create();
        TopMost = true;
        ShowInTaskbar = true;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(20);
        Font = new Font("Segoe UI", 10f);

        var hint = new Label
        {
            Text = "Так попросили с телефона. Сохраните работу или отмените.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
        };
        var cancelButton = new Button { Text = "Отменить", AutoSize = true, Padding = new Padding(12, 4, 12, 4) };
        cancelButton.Click += (_, _) =>
        {
            cancel();
            Close();
        };
        var hide = new Button { Text = "Скрыть", AutoSize = true, Padding = new Padding(12, 4, 12, 4) };
        hide.Click += (_, _) => Close();
        AcceptButton = cancelButton;

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 12, 0, 0) };
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(hide);

        var layout = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown };
        layout.Controls.Add(countdown);
        layout.Controls.Add(hint);
        layout.Controls.Add(buttons);
        Controls.Add(layout);

        tick.Tick += (_, _) => Update();
        tick.Start();
        FormClosed += (_, _) => tick.Dispose();
        Update();
    }

    new void Update()
    {
        var left = timer.At - DateTimeOffset.UtcNow;
        if (left < TimeSpan.Zero)
            left = TimeSpan.Zero;
        countdown.Text = $"{timer.Action.Describe()} через {(int)left.TotalMinutes}:{left.Seconds:00}";
    }
}

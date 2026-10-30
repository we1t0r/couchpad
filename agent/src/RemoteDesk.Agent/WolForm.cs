using RemoteDesk.Agent.Core;

namespace RemoteDesk.Agent;

/// <summary>Мастер «Включение по сети»: что проверено, что не так, кнопка «Исправить».</summary>
sealed class WolForm : Form
{
    readonly IWakeOnLanSetup wol;
    readonly TableLayoutPanel list = new() { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
    readonly Button fix = new() { Text = "Исправить (нужны права администратора)", AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
    readonly Button recheck = new() { Text = "Проверить ещё раз", AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
    readonly Label status = new() { AutoSize = true, MaximumSize = new Size(560, 0), ForeColor = SystemColors.GrayText };

    public WolForm(IWakeOnLanSetup wol)
    {
        this.wol = wol;
        Text = "Включение по сети — Couchpad";
        Icon = AppIcon.Create();
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(20);
        Font = new Font("Segoe UI", 10f);

        var title = new Label
        {
            Text = "Включать компьютер с телефона",
            Font = new Font("Segoe UI Semibold", 13f),
            AutoSize = true,
        };
        var intro = new Label
        {
            Text = "Сетевая карта может «слушать» сеть, даже когда компьютер выключен, и включить его по сигналу с телефона. "
                   + "Для этого нужно несколько настроек в Windows и одна в BIOS.",
            AutoSize = true,
            MaximumSize = new Size(560, 0),
        };
        var test = new Label
        {
            Text = "Как проверить: выключите компьютер (Пуск → Завершение работы), подождите 10 секунд и нажмите на телефоне «Включить» у этого компьютера.",
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            ForeColor = SystemColors.GrayText,
        };

        fix.Click += async (_, _) => await FixAsync();
        recheck.Click += async (_, _) => await RefreshAsync();
        var buttons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 12, 0, 0) };
        buttons.Controls.Add(fix);
        buttons.Controls.Add(recheck);

        var layout = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown };
        foreach (var c in new Control[] { title, intro, list, buttons, status, test })
        {
            c.Margin = new Padding(0, 6, 0, 6);
            layout.Controls.Add(c);
        }
        Controls.Add(layout);
        Shown += async (_, _) => await RefreshAsync();
    }

    async Task RefreshAsync()
    {
        recheck.Enabled = fix.Enabled = false;
        status.Text = "Проверяем…";
        // Проверка вызывает powercfg — не замораживаем окно.
        var report = await Task.Run(wol.Check);
        Render(report);
        recheck.Enabled = true;
    }

    void Render(WolReport report)
    {
        list.SuspendLayout();
        list.Controls.Clear();
        list.RowCount = 0;
        foreach (var check in report.Checks)
        {
            var (mark, color) = check.State switch
            {
                WolState.Ok => ("✓", Color.FromArgb(29, 158, 117)),
                WolState.Warning => ("!", Color.DarkOrange),
                WolState.Problem => ("✗", Color.Firebrick),
                _ => ("?", SystemColors.GrayText),
            };
            list.Controls.Add(new Label { Text = mark, ForeColor = color, AutoSize = true, Font = new Font("Segoe UI Semibold", 11f) });
            var text = check.Hint is null ? check.Title : $"{check.Title}\n{check.Hint}";
            list.Controls.Add(new Label { Text = text, AutoSize = true, MaximumSize = new Size(520, 0), Margin = new Padding(3, 3, 3, 9) });
        }
        list.ResumeLayout();
        fix.Visible = report.CanFix;
        fix.Enabled = report.CanFix;
        status.Text = report.Ready
            ? "Windows настроена. Осталось проверить BIOS (если ещё не делали) и попробовать включить с телефона."
            : report.CanFix
                ? "Нажмите «Исправить»: Windows спросит разрешение, сеть пропадёт на пару секунд."
                : "";
    }

    async Task FixAsync()
    {
        fix.Enabled = recheck.Enabled = false;
        status.Text = "Ждём разрешения администратора…";
        var done = await wol.FixAsync();
        if (!done)
            status.Text = "Не получилось: разрешение не дали. Можно попробовать ещё раз.";
        // Сетевая карта перезапускается после смены настроек — даём ей время.
        await Task.Delay(done ? 3000 : 0);
        await RefreshAsync();
    }
}

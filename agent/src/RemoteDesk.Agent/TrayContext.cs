using RemoteDesk.Agent.Core;

namespace RemoteDesk.Agent;

/// <summary>Значок в трее и его меню. Окна открываются по требованию и живут в одном экземпляре.</summary>
sealed class TrayContext : ApplicationContext
{
    readonly AgentHost host;
    readonly NotifyIcon tray;
    readonly SynchronizationContext ui;
    PairForm? pairForm;
    DevicesForm? devicesForm;
    PowerWarningForm? warningForm;
    readonly ToolStripMenuItem cancelTimerItem;

    public TrayContext(AgentHost host, bool showPairing)
    {
        this.host = host;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Подключить телефон…", null, (_, _) => ShowPairing());
        menu.Items.Add("Телефоны…", null, (_, _) => ShowDevices());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitThread());

        // Контекст синхронизации WinForms появляется после создания первого элемента управления.
        ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        tray = new NotifyIcon
        {
            Icon = AppIcon.Create(),
            Text = $"RemoteDesk — {host.Identity.Name}",
            ContextMenuStrip = menu,
            Visible = true,
        };
        tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                ShowDevicesOrPairing();
        };

        host.Server.Paired += device => ui.Post(_ =>
            tray.ShowBalloonTip(4000, "Телефон подключён", $"«{device.Name}» теперь может управлять компьютером.", ToolTipIcon.Info), null);

        // Таймер питания: пункт меню для отмены, уведомление при постановке, окно за минуту до срабатывания.
        cancelTimerItem = new ToolStripMenuItem("", null, (_, _) => host.Power.Cancel()) { Visible = false };
        menu.Items.Insert(0, cancelTimerItem);
        host.Power.Changed += timer => ui.Post(_ => OnTimerChanged(timer), null);
        host.Power.Warning += timer => ui.Post(_ => ShowWarning(timer), null);
        host.Power.Failed += (action, error) => ui.Post(_ =>
            tray.ShowBalloonTip(6000, "Не получилось", error, ToolTipIcon.Warning), null);

        if (showPairing)
            ShowPairing();
    }

    /// <summary>Действия, перед которыми стоит предупредить: можно потерять несохранённое.</summary>
    static bool NeedsWarning(PowerAction a) =>
        a is PowerAction.Shutdown or PowerAction.Restart or PowerAction.Sleep or PowerAction.Hibernate or PowerAction.SignOut;

    void OnTimerChanged(PowerTimer? timer)
    {
        if (timer is null)
        {
            cancelTimerItem.Visible = false;
            warningForm?.Close();
            return;
        }
        var at = timer.At.ToLocalTime().ToString("HH:mm");
        cancelTimerItem.Text = $"Отменить: {timer.Action.Describe().ToLowerInvariant()} в {at}";
        cancelTimerItem.Visible = true;
        if (NeedsWarning(timer.Action))
            tray.ShowBalloonTip(6000, $"{timer.Action.Describe()} в {at}",
                "Так попросили с телефона. Отменить можно в меню значка RemoteDesk или в приложении.", ToolTipIcon.Info);
    }

    void ShowWarning(PowerTimer timer)
    {
        if (!NeedsWarning(timer.Action))
            return;
        warningForm?.Close();
        warningForm = new PowerWarningForm(timer, () => host.Power.Cancel());
        warningForm.Show();
        warningForm.Activate();
    }

    void ShowDevicesOrPairing()
    {
        if (host.Devices.All.Count == 0)
            ShowPairing();
        else
            ShowDevices();
    }

    void ShowPairing()
    {
        if (pairForm is null || pairForm.IsDisposed)
            pairForm = new PairForm(host);
        pairForm.Show();
        pairForm.Activate();
    }

    void ShowDevices()
    {
        if (devicesForm is null || devicesForm.IsDisposed)
            devicesForm = new DevicesForm(host, ShowPairing);
        devicesForm.Show();
        devicesForm.Activate();
    }

    protected override void ExitThreadCore()
    {
        tray.Visible = false;
        tray.Dispose();
        pairForm?.Close();
        devicesForm?.Close();
        warningForm?.Close();
        base.ExitThreadCore();
    }
}

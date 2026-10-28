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

        if (showPairing)
            ShowPairing();
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
        base.ExitThreadCore();
    }
}

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
    WolForm? wolForm;
    SharesForm? sharesForm;
    PhoneFilesForm? phoneFilesForm;
    string? lastReceived;
    readonly ToolStripMenuItem cancelTimerItem;

    public TrayContext(AgentHost host, bool showPairing)
    {
        this.host = host;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Подключить телефон…", null, (_, _) => ShowPairing());
        menu.Items.Add("Телефоны…", null, (_, _) => ShowDevices());
        menu.Items.Add("Файлы телефона…", null, (_, _) => ShowPhoneFiles());
        menu.Items.Add("Папки для телефона…", null, (_, _) => ShowShares());
        menu.Items.Add("Включение по сети…", null, (_, _) => ShowWol());
        var autostartItem = new ToolStripMenuItem("Запускать вместе с Windows") { CheckOnClick = true };
        autostartItem.Click += (_, _) => Autostart.Set(autostartItem.Checked);
        menu.Opening += (_, _) => autostartItem.Checked = Autostart.IsEnabled;
        menu.Items.Add(autostartItem);
        var signInItem = new ToolStripMenuItem("Вход в Windows с телефона");
        signInItem.Click += async (_, _) => await ToggleSignInAsync();
        menu.Opening += (_, _) =>
        {
            signInItem.Checked = SignInSetup.IsInstalled;
            signInItem.Text = SignInSetup.IsOutdated ? "Вход в Windows с телефона — обновить" : "Вход в Windows с телефона";
        };
        menu.Items.Add(signInItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitThread());

        // Контекст синхронизации WinForms появляется после создания первого элемента управления.
        ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        tray = new NotifyIcon
        {
            Icon = AppIcon.Create(),
            Text = $"Couchpad — {host.Identity.Name}",
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
        host.Server.WolSetupRequested += () => ui.Post(_ => ShowWol(), null);
        host.Files.Received += path => ui.Post(_ =>
        {
            lastReceived = path;
            tray.ShowBalloonTip(5000, "Файл с телефона", $"{Path.GetFileName(path)} — сохранён в {Path.GetDirectoryName(path)}. Нажмите, чтобы открыть папку.", ToolTipIcon.Info);
        }, null);
        // Щелчок по уведомлению о файле — показать его в Проводнике.
        tray.BalloonTipClicked += (_, _) =>
        {
            if (lastReceived is { } path && File.Exists(path))
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"")?.Dispose();
        };
        tray.BalloonTipClosed += (_, _) => lastReceived = null;
        // Человек за компьютером должен знать, что его экран сейчас видят.
        host.Server.ScreenWatching += (phone, watching) => ui.Post(_ =>
        {
            tray.Text = watching ? $"Couchpad — «{phone}» смотрит экран" : $"Couchpad — {host.Identity.Name}";
            // При смене монитора показ перезапускается — не повторяем уведомление чаще раза в минуту.
            if (watching && DateTime.UtcNow - lastScreenBalloon > TimeSpan.FromMinutes(1))
            {
                lastScreenBalloon = DateTime.UtcNow;
                tray.ShowBalloonTip(4000, "Телефон смотрит экран", $"«{phone}» видит этот экран и может управлять мышью.", ToolTipIcon.Info);
            }
        }, null);
        host.Power.Failed += (action, error) => ui.Post(_ =>
            tray.ShowBalloonTip(6000, "Не получилось", error, ToolTipIcon.Warning), null);

        if (showPairing)
            ShowPairing();
    }

    DateTime lastScreenBalloon;
    readonly List<string> toSend = [];
    System.Windows.Forms.Timer? sendTimer;

    /// <summary>
    /// «Отправить на телефон» из Проводника. Можно звать из любого потока. Explorer запускает по копии
    /// программы на каждый выделенный файл, поэтому ждём полсекунды и отправляем всё одной пачкой.
    /// </summary>
    public void SendFiles(string[] paths) => ui.Post(_ =>
    {
        toSend.AddRange(paths);
        sendTimer ??= new System.Windows.Forms.Timer { Interval = 500 };
        sendTimer.Tick -= OnSendTimer;
        sendTimer.Tick += OnSendTimer;
        sendTimer.Stop();
        sendTimer.Start();
    }, null);

    async void OnSendTimer(object? sender, EventArgs e)
    {
        sendTimer!.Stop();
        var batch = toSend.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        toSend.Clear();
        var (files, phones) = await host.SendToPhoneAsync(batch);
        if (files == 0)
            tray.ShowBalloonTip(5000, "Нечего отправить", "На телефон можно отправить только файлы, не папки.", ToolTipIcon.Warning);
        else if (phones == 0)
            tray.ShowBalloonTip(6000, "Телефон не подключён",
                $"Файлов: {files}. Они уйдут, когда вы откроете компьютер в Couchpad на телефоне (в течение часа).", ToolTipIcon.Info);
        else
            tray.ShowBalloonTip(4000, "Отправлено на телефон",
                files == 1 ? Path.GetFileName(batch[0]) + " — телефон уже скачивает." : $"Файлов: {files} — телефон уже скачивает.", ToolTipIcon.Info);
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
                "Так попросили с телефона. Отменить можно в меню значка Couchpad или в приложении.", ToolTipIcon.Info);
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

    void ShowShares()
    {
        if (sharesForm is null || sharesForm.IsDisposed)
            sharesForm = new SharesForm(host.Shares);
        sharesForm.Show();
        sharesForm.Activate();
    }

    void ShowPhoneFiles()
    {
        if (phoneFilesForm is null || phoneFilesForm.IsDisposed)
            phoneFilesForm = new PhoneFilesForm(host);
        phoneFilesForm.Show();
        phoneFilesForm.Activate();
    }

    /// <summary>
    /// Включить, обновить или выключить службу входа. Ставит её копия агента с правами администратора —
    /// Windows спросит разрешения в окне UAC.
    /// </summary>
    async Task ToggleSignInAsync()
    {
        var install = !SignInSetup.IsInstalled || SignInSetup.IsOutdated;
        if (install && !SignInSetup.IsInstalled)
        {
            var answer = MessageBox.Show(
                "Телефон сможет показать экран входа и блокировки Windows и ввести на нём PIN или пароль — "
                + "например, после включения компьютера по сети.\n\n"
                + "Для этого Couchpad установит службу Windows. На экране входа телефону доступны только экран, "
                + "клавиши и питание, файлы и буфер обмена — после входа.\n\nWindows попросит разрешения администратора.",
                "Вход в Windows с телефона", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
            if (answer != DialogResult.OK)
                return;
        }
        var (started, code) = await SignInSetup.RunElevatedAsync(install, host.DataDir, host.Server.Port);
        if (!started || code != 0)
            return; // отказ в окне UAC или ошибка — о ней сообщило окно установки
        tray.ShowBalloonTip(5000, "Couchpad", install
            ? "Вход в Windows с телефона включён. Проверьте: заблокируйте компьютер (Win+L) и откройте «Экран» на телефоне."
            : "Вход в Windows с телефона выключен.", ToolTipIcon.Info);
    }

    void ShowWol()
    {
        if (wolForm is null || wolForm.IsDisposed)
            wolForm = new WolForm(host.Wol);
        wolForm.Show();
        wolForm.Activate();
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
        wolForm?.Close();
        sharesForm?.Close();
        phoneFilesForm?.Close();
        base.ExitThreadCore();
    }
}

using RemoteDesk.Agent.Core;

namespace RemoteDesk.Agent;

/// <summary>Окно установки: папка, автозапуск, ярлык, вход в Windows с телефона — и одна кнопка.</summary>
sealed class SetupForm : Form
{
    readonly TextBox folder = new() { Width = 420 };
    readonly Button browse = new() { Text = "Обзор…", AutoSize = true };
    readonly CheckBox autostart = new() { Text = "Запускать вместе с Windows", Checked = true, AutoSize = true };
    readonly CheckBox desktop = new() { Text = "Ярлык на рабочем столе", AutoSize = true };
    readonly CheckBox signIn = new() { Text = "Вход в Windows с телефона", AutoSize = true };
    readonly Button install = new() { AutoSize = true, Padding = new Padding(12, 3, 12, 3) };
    readonly Button cancel = new() { Text = "Отмена", AutoSize = true, Padding = new Padding(8, 3, 8, 3) };
    readonly Label status = new() { AutoSize = true, MaximumSize = new Size(520, 0), ForeColor = SystemColors.GrayText };

    public SetupForm()
    {
        var installed = Installer.InstalledDir;
        Text = "Установка Couchpad " + Protocol.AgentVersion;
        Icon = AppIcon.Create();
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(20);
        Font = new Font("Segoe UI", 10f);

        var title = new Label { Text = "Couchpad — пульт для компьютера с телефона", Font = new Font("Segoe UI Semibold", 13f), AutoSize = true };
        var intro = new Label
        {
            Text = installed is null
                ? "Тачпад, клавиатура, файлы, буфер обмена, экран и звук компьютера — с телефона. После установки откроется окно с QR-кодом: отсканируйте его приложением Couchpad на телефоне."
                : $"Couchpad уже установлен — обновим его. Сопряжённые телефоны и настройки сохранятся.",
            AutoSize = true,
            MaximumSize = new Size(520, 0),
        };

        folder.Text = installed ?? Installer.DefaultDir;
        folder.Enabled = browse.Enabled = installed is null;
        browse.Click += (_, _) => Browse();
        var folderRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        folderRow.Controls.Add(folder);
        folderRow.Controls.Add(browse);
        var folderLabel = new Label { Text = "Папка установки:", AutoSize = true };

        signIn.Checked = SignInSetup.IsInstalled;
        var signInHint = new Label
        {
            Text = "Экран входа и блокировки Windows на телефоне — например, ввести PIN после включения компьютера по сети. Ставит службу Windows.",
            AutoSize = true,
            MaximumSize = new Size(500, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(20, 0, 0, 6),
        };

        install.Text = installed is null ? "Установить" : "Обновить";
        install.Click += async (_, _) => await InstallAsync();
        cancel.Click += (_, _) => Close();
        AcceptButton = install;
        CancelButton = cancel;
        var buttons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 12, 0, 0) };
        buttons.Controls.Add(install);
        buttons.Controls.Add(cancel);

        var hint = new Label
        {
            Text = "Windows попросит разрешения администратора: Couchpad ставится в Program Files и сам разрешает себя в брандмауэре для домашней сети.",
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            ForeColor = SystemColors.GrayText,
        };

        var layout = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        foreach (var c in new Control[] { title, intro, folderLabel, folderRow, autostart, desktop, signIn, signInHint, hint, buttons, status })
        {
            if (c.Margin == DefaultMargin)
                c.Margin = new Padding(0, 4, 0, 4);
            layout.Controls.Add(c);
        }
        Controls.Add(layout);
    }

    void Browse()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Куда установить Couchpad (внутри будет папка Couchpad)",
            UseDescriptionForTitle = true,
            InitialDirectory = Path.GetDirectoryName(folder.Text.TrimEnd('\\')) ?? "",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            folder.Text = Installer.WithAppFolder(dialog.SelectedPath);
    }

    async Task InstallAsync()
    {
        string dir;
        try
        {
            dir = Installer.WithAppFolder(Path.GetFullPath(folder.Text.Trim()));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            status.Text = "Не похоже на папку. Нажмите «Обзор…» и выберите.";
            return;
        }
        foreach (var c in new Control[] { install, cancel, browse, folder, autostart, desktop, signIn })
            c.Enabled = false;
        status.ForeColor = SystemColors.GrayText;
        status.Text = "Устанавливаем…";
        var (ok, error) = await Installer.InstallAsync(dir, autostart.Checked, desktop.Checked, signIn.Checked);
        if (ok)
        {
            Close();
            return;
        }
        status.ForeColor = Color.Firebrick;
        status.Text = error ?? "Не получилось. Попробуйте ещё раз.";
        foreach (var c in new Control[] { install, cancel, autostart, desktop, signIn })
            c.Enabled = true;
        folder.Enabled = browse.Enabled = Installer.InstalledDir is null;
    }
}

using System.Runtime.InteropServices;
using Couchpad.Agent.Core;
using Keys = System.Windows.Forms.Keys;

namespace Couchpad.Agent;

/// <summary>
/// «Файлы телефона»: папки, которые человек открыл компьютеру в приложении на телефоне.
/// Скачать на ПК, отправить на телефон (кнопкой или перетаскиванием), создать папку, переименовать, удалить.
/// </summary>
sealed class PhoneFilesForm : Form
{
    readonly AgentHost host;
    readonly PhoneFiles phone;
    readonly ComboBox phones = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    readonly Label where = new() { AutoSize = true, Padding = new Padding(8, 6, 0, 0) };
    readonly Button up = new() { Text = "↑ Наверх", AutoSize = true, Enabled = false };
    readonly Button refresh = new() { Text = "Обновить", AutoSize = true };
    readonly ListView list = new()
    {
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = true,
        HideSelection = false,
        Dock = DockStyle.Fill,
        AllowDrop = true,
    };
    readonly Button download = new() { Text = "Скачать на ПК…", AutoSize = true, Enabled = false };
    readonly Button send = new() { Text = "Отправить на телефон…", AutoSize = true, Enabled = false };
    readonly Button newFolder = new() { Text = "Новая папка", AutoSize = true, Enabled = false };
    readonly Button rename = new() { Text = "Переименовать", AutoSize = true, Enabled = false };
    readonly Button delete = new() { Text = "Удалить", AutoSize = true, Enabled = false };
    readonly Label status = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Padding = new Padding(0, 8, 0, 0), MaximumSize = new Size(820, 0) };

    // Где мы: null — список папок телефона, иначе папка и путь внутри неё.
    PhoneRoot? root;
    string path = "";
    string? lastFolder;
    bool busy;

    string? DeviceId => (phones.SelectedItem as PhoneItem)?.Device.DeviceId;

    sealed record PhoneItem(PairedDevice Device)
    {
        public override string ToString() => Device.Name;
    }

    public PhoneFilesForm(AgentHost host)
    {
        this.host = host;
        phone = host.PhoneFiles;

        Text = "Файлы телефона — Couchpad";
        Icon = AppIcon.Create();
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Size = new Size(860, 560);
        MinimumSize = new Size(640, 400);
        Font = new Font("Segoe UI", 10f);
        Padding = new Padding(12);

        list.Columns.Add("Имя", 420);
        list.Columns.Add("Размер", 110, HorizontalAlignment.Right);
        list.Columns.Add("Изменён", 180);
        list.SmallImageList = ShellIcons.Create();
        list.DoubleClick += (_, _) => OpenSelected();
        list.SelectedIndexChanged += (_, _) => UpdateButtons();
        list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Back) GoUp();
            else if (e.KeyCode == Keys.Enter) OpenSelected();
            else if (e.KeyCode == Keys.Delete) DeleteSelected();
            else if (e.KeyCode == Keys.F5) _ = LoadAsync();
        };
        // Перетащили файлы из Проводника — отправить их в открытую папку телефона.
        list.DragEnter += (_, e) => e.Effect = CanWrite && e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        list.DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] dropped)
                _ = SendAsync(dropped);
        };

        phones.SelectedIndexChanged += (_, _) =>
        {
            root = null;
            path = "";
            _ = LoadAsync();
        };
        up.Click += (_, _) => GoUp();
        refresh.Click += (_, _) => _ = LoadAsync();
        download.Click += (_, _) => _ = DownloadAsync();
        send.Click += (_, _) => PickAndSend();
        newFolder.Click += (_, _) => _ = NewFolderAsync();
        rename.Click += (_, _) => _ = RenameAsync();
        delete.Click += (_, _) => DeleteSelected();

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(0, 0, 0, 8) };
        top.Controls.Add(new Label { Text = "Телефон:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
        top.Controls.Add(phones);
        top.Controls.Add(up);
        top.Controls.Add(refresh);
        top.Controls.Add(where);

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.TopDown, Padding = new Padding(0, 8, 0, 0) };
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(820, 0) };
        buttons.Controls.AddRange([download, send, newFolder, rename, delete]);
        bottom.Controls.Add(buttons);
        bottom.Controls.Add(status);

        Controls.Add(list);
        Controls.Add(top);
        Controls.Add(bottom);

        host.Server.ConnectionsChanged += OnConnectionsChanged;
        host.Files.Pulled += OnPulled;
        FormClosed += (_, _) =>
        {
            host.Server.ConnectionsChanged -= OnConnectionsChanged;
            host.Files.Pulled -= OnPulled;
        };
        FillPhones();
    }

    bool CanWrite => root?.Writable == true;

    void OnConnectionsChanged() => BeginInvokeSafe(FillPhones);

    void OnPulled(string file) => BeginInvokeSafe(() => Status($"Скачано на ПК: {file}"));

    void BeginInvokeSafe(Action action)
    {
        if (IsHandleCreated && !IsDisposed)
            BeginInvoke(action);
    }

    void FillPhones()
    {
        var selected = DeviceId;
        var connected = phone.Connected();
        phones.BeginUpdate();
        phones.Items.Clear();
        foreach (var d in connected)
            phones.Items.Add(new PhoneItem(d));
        phones.EndUpdate();
        if (connected.Count == 0)
        {
            root = null;
            list.Items.Clear();
            where.Text = "";
            Status("Телефон не подключён. Откройте этот компьютер в Couchpad на телефоне — список появится сам.");
            UpdateButtons();
            return;
        }
        var index = connected.ToList().FindIndex(d => d.DeviceId == selected);
        phones.SelectedIndex = index >= 0 ? index : 0; // вызовет загрузку
    }

    async Task LoadAsync()
    {
        if (DeviceId is not { } id)
            return;
        await Run(async () =>
        {
            list.Items.Clear();
            if (root is null)
            {
                var roots = await phone.RootsAsync(id);
                foreach (var r in roots)
                {
                    var item = new ListViewItem(r.Name, ShellIcons.Folder) { Tag = r };
                    item.SubItems.Add("");
                    item.SubItems.Add(r.Writable ? "можно записывать" : "только просмотр");
                    list.Items.Add(item);
                }
                where.Text = "Папки телефона";
                Status(roots.Count == 0
                    ? "На телефоне не выбрано ни одной папки. Добавьте их в Couchpad на телефоне: Настройки → «Файлы телефона на компьютере»."
                    : "Двойной щелчок — открыть папку.");
            }
            else
            {
                var entries = await phone.ListAsync(id, root.Id, path);
                foreach (var e in entries.OrderBy(e => !e.Dir).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase))
                {
                    var item = new ListViewItem(e.Name, e.Dir ? ShellIcons.Folder : ShellIcons.File) { Tag = e };
                    item.SubItems.Add(e.Dir ? "" : FormatSize(e.Size));
                    item.SubItems.Add(e.Modified.ToUnixTimeMilliseconds() > 0 ? e.Modified.ToLocalTime().ToString("g") : "");
                    list.Items.Add(item);
                }
                where.Text = path.Length == 0 ? root.Name : $"{root.Name} / {path.Replace("/", " / ")}";
                Status(CanWrite
                    ? "Файлы с ПК можно перетащить сюда мышью — телефон скачает их в эту папку."
                    : "Папка открыта только для просмотра — разрешить запись можно в приложении на телефоне.");
            }
        });
        UpdateButtons();
    }

    void UpdateButtons()
    {
        var selected = list.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag).ToList();
        var inFolder = root is not null && !busy;
        up.Enabled = root is not null && !busy;
        download.Enabled = inFolder && selected.Count > 0 && selected.All(t => t is FileEntry { Dir: false });
        send.Enabled = inFolder && CanWrite;
        newFolder.Enabled = inFolder && CanWrite;
        rename.Enabled = inFolder && CanWrite && selected.Count == 1;
        delete.Enabled = inFolder && CanWrite && selected.Count > 0;
        refresh.Enabled = !busy && DeviceId is not null;
    }

    void GoUp()
    {
        if (root is null || busy)
            return;
        if (path.Length == 0)
            root = null;
        else
            path = path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
        _ = LoadAsync();
    }

    void OpenSelected()
    {
        if (busy || list.SelectedItems.Count != 1)
            return;
        switch (list.SelectedItems[0].Tag)
        {
            case PhoneRoot r:
                root = r;
                path = "";
                _ = LoadAsync();
                break;
            case FileEntry { Dir: true } dir:
                path = Join(path, dir.Name);
                _ = LoadAsync();
                break;
            case FileEntry:
                _ = DownloadAsync();
                break;
        }
    }

    async Task DownloadAsync()
    {
        if (root is null || DeviceId is not { } id)
            return;
        var picked = list.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag).OfType<FileEntry>().Where(e => !e.Dir).ToList();
        if (picked.Count == 0)
            return;
        using var dialog = new FolderBrowserDialog
        {
            Description = "Куда сохранить файлы с телефона?",
            UseDescriptionForTitle = true,
            InitialDirectory = lastFolder ?? host.Shares.InboxPath,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        lastFolder = dialog.SelectedPath;
        await Run(async () =>
        {
            foreach (var e in picked)
                await phone.DownloadAsync(id, root.Id, Join(path, e.Name), dialog.SelectedPath);
            Status(picked.Count == 1
                ? $"Телефон отправляет «{picked[0].Name}» — прогресс виден на телефоне."
                : $"Телефон отправляет файлов: {picked.Count} — прогресс виден на телефоне.");
        });
    }

    void PickAndSend()
    {
        using var dialog = new OpenFileDialog { Title = "Какие файлы отправить на телефон?", Multiselect = true };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _ = SendAsync(dialog.FileNames);
    }

    async Task SendAsync(string[] files)
    {
        if (root is null || DeviceId is not { } id || !CanWrite)
            return;
        var only = files.Where(File.Exists).ToList();
        if (only.Count == 0)
        {
            Status("Отправить на телефон можно только файлы, не папки.");
            return;
        }
        await Run(async () =>
        {
            foreach (var file in only)
                await phone.UploadAsync(id, root.Id, path, file);
            Status($"Телефон скачивает файлов: {only.Count}. Нажмите «Обновить», когда закончит.");
        });
    }

    async Task NewFolderAsync()
    {
        if (root is null || DeviceId is not { } id)
            return;
        var name = Prompt.Ask(this, "Новая папка", "Название папки:", "Новая папка");
        if (name is null)
            return;
        await Run(() => phone.MkdirAsync(id, root.Id, Join(path, name)));
        await LoadAsync();
    }

    async Task RenameAsync()
    {
        if (root is null || DeviceId is not { } id || list.SelectedItems.Count != 1 || list.SelectedItems[0].Tag is not FileEntry e)
            return;
        var name = Prompt.Ask(this, "Переименовать", "Новое имя:", e.Name);
        if (name is null || name == e.Name)
            return;
        await Run(() => phone.RenameAsync(id, root.Id, Join(path, e.Name), name));
        await LoadAsync();
    }

    async void DeleteSelected()
    {
        if (root is null || DeviceId is not { } id || !CanWrite)
            return;
        var picked = list.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag).OfType<FileEntry>().ToList();
        if (picked.Count == 0)
            return;
        var what = picked.Count == 1 ? $"«{picked[0].Name}»" : $"файлов и папок: {picked.Count}";
        if (MessageBox.Show(this, $"Удалить с телефона {what}?\n\nУ телефона нет корзины — вернуть не получится.",
                "Couchpad", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        await Run(async () =>
        {
            foreach (var e in picked)
                await phone.DeleteAsync(id, root.Id, Join(path, e.Name));
        });
        await LoadAsync();
    }

    /// <summary>Одна операция за раз; ошибку телефона показываем внизу окна.</summary>
    async Task Run(Func<Task> work)
    {
        busy = true;
        UseWaitCursor = true;
        UpdateButtons();
        try
        {
            await work();
        }
        catch (PhoneFilesException e)
        {
            Status(e.Message, error: true);
        }
        finally
        {
            busy = false;
            UseWaitCursor = false;
            UpdateButtons();
        }
    }

    void Status(string text, bool error = false)
    {
        status.Text = text;
        status.ForeColor = error ? Color.Firebrick : SystemColors.GrayText;
    }

    static string Join(string path, string name) => path.Length == 0 ? name : $"{path}/{name}";

    static string FormatSize(long bytes)
    {
        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} Б" : $"{value:0.#} {units[unit]}";
    }
}

/// <summary>Окно с одним полем ввода (в WinForms нет готового).</summary>
static class Prompt
{
    public static string? Ask(IWin32Window owner, string title, string label, string initial)
    {
        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            AutoScaleMode = AutoScaleMode.Dpi,
            Font = new Font("Segoe UI", 10f),
            ClientSize = new Size(380, 130),
            ShowInTaskbar = false,
        };
        var text = new TextBox { Text = initial, Location = new Point(12, 36), Width = 356 };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(196, 84), AutoSize = true };
        var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, Location = new Point(284, 84), AutoSize = true };
        form.Controls.AddRange([new Label { Text = label, Location = new Point(12, 10), AutoSize = true }, text, ok, cancel]);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        form.Shown += (_, _) =>
        {
            // Выделяем имя без расширения — как в Проводнике.
            var dot = initial.LastIndexOf('.');
            text.Select(0, dot > 0 ? dot : initial.Length);
        };
        return form.ShowDialog(owner) == DialogResult.OK && !string.IsNullOrWhiteSpace(text.Text) ? text.Text.Trim() : null;
    }
}

/// <summary>Значки папки и файла из Windows (те же, что в Проводнике).</summary>
static class ShellIcons
{
    public const int Folder = 0, File = 1;

    public static ImageList Create()
    {
        var images = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(16, 16) };
        images.Images.Add(Stock(3) ?? SystemIcons.WinLogo); // SIID_FOLDER
        images.Images.Add(Stock(0) ?? SystemIcons.Application); // SIID_DOCNOASSOC
        return images;
    }

    static Icon? Stock(uint id)
    {
        var info = new StockIconInfo { cbSize = (uint)Marshal.SizeOf<StockIconInfo>() };
        if (SHGetStockIconInfo(id, 0x100 | 0x1 /* SHGSI_ICON | SHGSI_SMALLICON */, ref info) != 0 || info.hIcon == 0)
            return null;
        var icon = (Icon)Icon.FromHandle(info.hIcon).Clone();
        DestroyIcon(info.hIcon);
        return icon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct StockIconInfo
    {
        public uint cbSize;
        public nint hIcon;
        public int iSysImageIndex;
        public int iIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szPath;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHGetStockIconInfo(uint siid, uint flags, ref StockIconInfo info);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(nint handle);
}

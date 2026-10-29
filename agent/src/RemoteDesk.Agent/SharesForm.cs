using RemoteDesk.Agent.Core;

namespace RemoteDesk.Agent;

/// <summary>«Папки для телефона»: что видно с телефона и можно ли туда записывать.</summary>
sealed class SharesForm : Form
{
    readonly ShareStore shares;
    readonly ListView list = new()
    {
        View = View.Details,
        CheckBoxes = true,
        FullRowSelect = true,
        MultiSelect = false,
        HideSelection = false,
        Dock = DockStyle.Fill,
    };
    readonly CheckBox whole = new() { Text = "Весь компьютер (все диски)", AutoSize = true };
    readonly CheckBox wholeWritable = new() { Text = "…и разрешить запись на дисках", AutoSize = true, Margin = new Padding(24, 0, 0, 0) };
    readonly Button remove = new() { Text = "Убрать", AutoSize = true, Enabled = false };
    List<ShareRoot> folders;

    public SharesForm(ShareStore shares)
    {
        this.shares = shares;
        folders = shares.Folders.ToList();

        Text = "Папки для телефона — RemoteDesk";
        Icon = AppIcon.Create();
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Size = new Size(700, 460);
        MinimumSize = new Size(560, 360);
        Font = new Font("Segoe UI", 10f);
        Padding = new Padding(12);

        var intro = new Label
        {
            Text = "Телефон видит только эти папки. Галочка — «можно записывать»: загружать файлы, переименовывать, "
                   + "удалять (в корзину). Без галочки — только смотреть и скачивать.",
            Dock = DockStyle.Top,
            AutoSize = true,
            MaximumSize = new Size(660, 0),
            Padding = new Padding(0, 0, 0, 8),
        };

        list.Columns.Add("Папка (галочка — запись)", 220);
        list.Columns.Add("Путь", 400);
        list.ItemChecked += (_, _) => Save();
        list.SelectedIndexChanged += (_, _) => remove.Enabled = list.SelectedItems.Count > 0;

        var add = new Button { Text = "Добавить папку…", AutoSize = true };
        add.Click += (_, _) => AddFolder();
        remove.Click += (_, _) => RemoveSelected();

        whole.Checked = shares.WholeComputer;
        wholeWritable.Checked = shares.WholeComputerWritable;
        wholeWritable.Enabled = whole.Checked;
        whole.CheckedChanged += (_, _) =>
        {
            wholeWritable.Enabled = whole.Checked;
            Save();
        };
        wholeWritable.CheckedChanged += (_, _) => Save();

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.TopDown, Padding = new Padding(0, 8, 0, 0) };
        var buttons = new FlowLayoutPanel { AutoSize = true };
        buttons.Controls.Add(add);
        buttons.Controls.Add(remove);
        bottom.Controls.Add(buttons);
        bottom.Controls.Add(whole);
        bottom.Controls.Add(wholeWritable);
        bottom.Controls.Add(new Label
        {
            Text = $"Файлы, отправленные с телефона кнопкой «Поделиться», сохраняются в: {shares.InboxPath}",
            AutoSize = true,
            MaximumSize = new Size(660, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 8, 0, 0),
        });

        Controls.Add(list);
        Controls.Add(intro);
        Controls.Add(bottom);
        Reload();
    }

    bool loading;

    void Reload()
    {
        loading = true;
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var f in folders)
        {
            var item = new ListViewItem(f.Name) { Tag = f.Id, Checked = f.Writable };
            item.SubItems.Add(f.Path);
            list.Items.Add(item);
        }
        list.EndUpdate();
        loading = false;
    }

    void Save()
    {
        if (loading)
            return;
        folders = folders.Select(f =>
        {
            var item = list.Items.Cast<ListViewItem>().FirstOrDefault(i => (string)i.Tag! == f.Id);
            return item is null ? f : f with { Writable = item.Checked };
        }).ToList();
        shares.Update(folders, whole.Checked, wholeWritable.Checked);
    }

    void AddFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "Какую папку открыть телефону?", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        if (folders.Any(f => string.Equals(f.Path, Path.GetFullPath(dialog.SelectedPath), StringComparison.OrdinalIgnoreCase)))
            return;
        folders.Add(ShareStore.NewFolder(dialog.SelectedPath, writable: false, folders));
        shares.Update(folders, whole.Checked, wholeWritable.Checked);
        Reload();
    }

    void RemoveSelected()
    {
        if (list.SelectedItems.Count == 0 || list.SelectedItems[0].Tag is not string id)
            return;
        folders.RemoveAll(f => f.Id == id);
        shares.Update(folders, whole.Checked, wholeWritable.Checked);
        Reload();
    }
}

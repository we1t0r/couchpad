using Couchpad.Agent.Core;

namespace Couchpad.Agent;

/// <summary>Список сопряжённых телефонов: кто в сети, когда был, отзыв доступа.</summary>
sealed class DevicesForm : Form
{
    readonly AgentHost host;
    readonly ListView list = new()
    {
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
        HideSelection = false,
        Dock = DockStyle.Fill,
    };
    readonly Button revoke = new() { Text = "Отозвать доступ", AutoSize = true, Enabled = false };
    readonly SynchronizationContext ui;

    public DevicesForm(AgentHost host, Action showPairing)
    {
        this.host = host;
        ui = SynchronizationContext.Current!;

        Text = "Телефоны — Couchpad";
        Icon = AppIcon.Create();
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Size = new Size(680, 380);
        MinimumSize = new Size(520, 260);
        Font = new Font("Segoe UI", 10f);
        Padding = new Padding(12);

        list.Columns.Add("Телефон", 200);
        list.Columns.Add("Система", 90);
        list.Columns.Add("Статус", 90);
        list.Columns.Add("Подключён", 130);
        list.Columns.Add("Последний раз", 130);
        list.SelectedIndexChanged += (_, _) => revoke.Enabled = list.SelectedItems.Count > 0;

        var add = new Button { Text = "Подключить телефон…", AutoSize = true };
        add.Click += (_, _) => showPairing();
        revoke.Click += async (_, _) => await RevokeSelectedAsync();

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 8, 0, 0),
        };
        buttons.Controls.Add(add);
        buttons.Controls.Add(revoke);

        Controls.Add(list);
        Controls.Add(buttons);

        host.Devices.Changed += OnChanged;
        host.Server.ConnectionsChanged += OnChanged;
        FormClosed += (_, _) =>
        {
            host.Devices.Changed -= OnChanged;
            host.Server.ConnectionsChanged -= OnChanged;
        };
        Reload();
    }

    void OnChanged() => ui.Post(_ => Reload(), null);

    void Reload()
    {
        var selected = list.SelectedItems.Count > 0 ? (string)list.SelectedItems[0].Tag! : null;
        var online = host.Server.ConnectedDeviceIds.ToHashSet();
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var device in host.Devices.All.OrderByDescending(d => d.LastSeen))
        {
            var item = new ListViewItem(device.Name) { Tag = device.DeviceId };
            item.SubItems.Add(device.Platform == "android" ? "Android" : device.Platform);
            item.SubItems.Add(online.Contains(device.DeviceId) ? "в сети" : "—");
            item.SubItems.Add(device.PairedAt.ToLocalTime().ToString("g"));
            item.SubItems.Add(device.LastSeen?.ToLocalTime().ToString("g") ?? "—");
            item.Selected = device.DeviceId == selected;
            list.Items.Add(item);
        }
        if (list.Items.Count == 0)
            list.Items.Add(new ListViewItem("Пока ни одного телефона") { ForeColor = SystemColors.GrayText, Tag = "" });
        list.EndUpdate();
        revoke.Enabled = list.SelectedItems.Count > 0 && (string)list.SelectedItems[0].Tag! != "";
    }

    async Task RevokeSelectedAsync()
    {
        if (list.SelectedItems.Count == 0 || list.SelectedItems[0].Tag is not string id || id == "")
            return;
        var name = list.SelectedItems[0].Text;
        var answer = MessageBox.Show(
            $"Отозвать доступ у «{name}»?\n\nТелефон сразу отключится и больше не сможет управлять компьютером, пока его не подключат заново по QR-коду.",
            "Couchpad", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer == DialogResult.Yes)
            await host.RevokeAsync(id);
    }
}

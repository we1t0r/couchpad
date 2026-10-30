using QRCoder;
using RemoteDesk.Agent.Core;

namespace RemoteDesk.Agent;

/// <summary>Окно сопряжения: QR-код для приложения на телефоне и его срок жизни.</summary>
sealed class PairForm : Form
{
    readonly AgentHost host;
    readonly PictureBox qr = new() { SizeMode = PictureBoxSizeMode.Zoom, Width = 300, Height = 300, Anchor = AnchorStyles.None };
    readonly Label address = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Anchor = AnchorStyles.None };
    readonly Label expires = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Anchor = AnchorStyles.None };
    readonly Label status = new() { AutoSize = true, Anchor = AnchorStyles.None };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    readonly SynchronizationContext ui;

    public PairForm(AgentHost host)
    {
        this.host = host;
        ui = SynchronizationContext.Current!;

        Text = "Подключить телефон — Couchpad";
        Icon = AppIcon.Create();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(24);
        Font = new Font("Segoe UI", 10f);

        var title = new Label
        {
            Text = "Отсканируйте код в приложении Couchpad",
            Font = new Font("Segoe UI Semibold", 13f),
            AutoSize = true,
            Anchor = AnchorStyles.None,
        };
        var hint = new Label
        {
            Text = "Откройте приложение на телефоне → «Добавить компьютер» → «Сканировать QR-код».\n"
                   + "Телефон и компьютер должны быть в одной сети (например, в одном Wi-Fi).",
            AutoSize = true,
            MaximumSize = new Size(420, 0),
            TextAlign = ContentAlignment.MiddleCenter,
            Anchor = AnchorStyles.None,
        };
        var refresh = new Button { Text = "Обновить код", AutoSize = true, Anchor = AnchorStyles.None };
        refresh.Click += (_, _) => host.Secrets.Rotate();

        var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        foreach (var control in new Control[] { title, hint, qr, address, expires, status, refresh })
        {
            control.Margin = new Padding(0, 6, 0, 6);
            layout.Controls.Add(control);
        }
        Controls.Add(layout);

        host.Secrets.Changed += OnSecretChanged;
        host.Server.Paired += OnPaired;
        timer.Tick += (_, _) => UpdateExpiry();
        timer.Start();
        FormClosed += (_, _) =>
        {
            timer.Dispose();
            host.Secrets.Changed -= OnSecretChanged;
            host.Server.Paired -= OnPaired;
        };

        Redraw();
        SetStatus("Ожидаем телефон…", SystemColors.ControlText);
    }

    void OnSecretChanged() => ui.Post(_ => Redraw(), null);

    void OnPaired(PairedDevice device) => ui.Post(_ =>
        SetStatus($"✓ Подключён «{device.Name}». Можно подключить ещё один телефон.", Color.FromArgb(29, 158, 117)), null);

    void Redraw()
    {
        var addresses = NetworkInfo.Addresses();
        if (addresses.Count == 0)
        {
            qr.Image = null;
            address.Text = "Компьютер не подключён к сети. Подключите кабель или Wi-Fi.";
            address.ForeColor = Color.Firebrick;
            return;
        }

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(host.PairingLink(addresses), QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(10);
        var old = qr.Image;
        qr.Image = Image.FromStream(new MemoryStream(png));
        old?.Dispose();

        address.ForeColor = SystemColors.GrayText;
        address.Text = $"{host.Identity.Name} · {string.Join(", ", addresses)} · порт {host.Server.Port}";
        UpdateExpiry();
    }

    void UpdateExpiry()
    {
        // Чтение Current само обновит ключ, если срок вышел (и вызовет Redraw через Changed).
        var left = host.Secrets.Current.Expires - DateTimeOffset.UtcNow;
        expires.Text = $"Код действует ещё {Math.Max(0, (int)left.TotalMinutes)}:{Math.Max(0, left.Seconds):00} и сработает один раз";
    }

    void SetStatus(string text, Color color)
    {
        status.Text = text;
        status.ForeColor = color;
    }
}

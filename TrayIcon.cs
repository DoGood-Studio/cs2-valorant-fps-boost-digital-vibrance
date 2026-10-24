using System;
using System.Drawing;
using System.IO;
using GameColorGuard.Core;
using Forms = System.Windows.Forms;

namespace GameColorGuard;

/// <summary>Koyu temalı menüye sahip sistem tepsisi simgesi.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Icon _icon;
    private bool _disposed;

    public event EventHandler? OpenRequested;
    public event EventHandler? RestoreRequested;
    public event EventHandler? ExitRequested;

    public TrayIcon()
    {
        _icon = LoadIcon();
        var menu = new Forms.ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(),
            ShowImageMargin = false,
            BackColor = Color.FromArgb(26, 30, 37),
            ForeColor = Color.FromArgb(232, 234, 239),
            Font = new Font("Segoe UI", 9.5F),
            Padding = new Forms.Padding(4)
        };
        AddItem(menu, "Pencereyi aç", () => OpenRequested?.Invoke(this, EventArgs.Empty), bold: true);
        AddItem(menu, "Renkleri geri yükle", () => RestoreRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(new Forms.ToolStripSeparator());
        AddItem(menu, "Çıkış", () => ExitRequested?.Invoke(this, EventArgs.Empty));

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _icon,
            Text = "Game Color Guard",
            Visible = true,
            ContextMenuStrip = menu
        };
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) OpenRequested?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>Menü öğelerini seçili dile çevirir (Türkçe asıl Tag'de durur).</summary>
    public void Retranslate()
    {
        if (_disposed || _notifyIcon.ContextMenuStrip is null) return;
        foreach (Forms.ToolStripItem item in _notifyIcon.ContextMenuStrip.Items)
            if (item.Tag is string source) item.Text = Loc.T(source);
    }

    public void SetText(string text)
    {
        if (_disposed) return;
        _notifyIcon.Text = text.Length <= 63 ? text : "Game Color Guard";
    }

    public void ShowBalloon(string title, string text)
    {
        if (_disposed) return;
        _notifyIcon.ShowBalloonTip(1800, title, text, Forms.ToolTipIcon.Info);
    }

    private static void AddItem(Forms.ContextMenuStrip menu, string text, Action action, bool bold = false)
    {
        var item = new Forms.ToolStripMenuItem(Loc.T(text))
        {
            Tag = text,
            Padding = new Forms.Padding(8, 6, 16, 6)
        };
        if (bold) item.Font = new Font("Segoe UI Semibold", 9.5F, FontStyle.Bold);
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private static Icon LoadIcon()
    {
        try
        {
            var info = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/assets/app-icon.ico", UriKind.Absolute));
            if (info is not null)
            {
                using Stream stream = info.Stream;
                return new Icon(stream, Forms.SystemInformation.SmallIconSize);
            }
        }
        catch
        {
            // Aşağıdaki yedeklere düş.
        }

        try
        {
            string? path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                Icon? associated = Icon.ExtractAssociatedIcon(path);
                if (associated is not null) return associated;
            }
        }
        catch
        {
            // Sistem simgesine düş.
        }
        return (Icon)SystemIcons.Shield.Clone();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
        _icon.Dispose();
    }

    private sealed class DarkMenuRenderer : Forms.ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkColors())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Selected ? Color.White : Color.FromArgb(232, 234, 239);
            base.OnRenderItemText(e);
        }
    }

    private sealed class DarkColors : Forms.ProfessionalColorTable
    {
        private static readonly Color Background = Color.FromArgb(26, 30, 37);
        private static readonly Color Border = Color.FromArgb(46, 52, 64);
        private static readonly Color Hover = Color.FromArgb(52, 38, 28);

        public override Color ToolStripDropDownBackground => Background;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Color.FromArgb(120, 70, 30);
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Hover;
        public override Color MenuItemPressedGradientEnd => Hover;
        public override Color ImageMarginGradientBegin => Background;
        public override Color ImageMarginGradientMiddle => Background;
        public override Color ImageMarginGradientEnd => Background;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Background;
    }
}

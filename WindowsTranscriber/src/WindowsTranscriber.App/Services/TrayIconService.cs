using Forms = System.Windows.Forms;

namespace WindowsTranscriber.App.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ToolStripMenuItem _toggleItem;
    private bool _trayModeEnabled;
    private bool _notificationsEnabled;

    public TrayIconService()
    {
        _toggleItem = new Forms.ToolStripMenuItem("Start transcription");
        _toggleItem.Click += (_, _) => ToggleRequested?.Invoke();

        var showItem = new Forms.ToolStripMenuItem("Show WindowsTranscriber");
        showItem.Click += (_, _) => ShowRequested?.Invoke();
        var exitItem = new Forms.ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitRequested?.Invoke();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(showItem);
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "WindowsTranscriber",
            Icon = System.Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
        };
        _notifyIcon.DoubleClick += (_, _) => ShowRequested?.Invoke();
    }

    public event Action? ShowRequested;

    public event Action? ToggleRequested;

    public event Action? ExitRequested;

    public void Configure(bool trayModeEnabled, bool notificationsEnabled)
    {
        _trayModeEnabled = trayModeEnabled;
        _notificationsEnabled = notificationsEnabled;
        _notifyIcon.Visible = _trayModeEnabled || _notificationsEnabled;
    }

    public void UpdateTranscriptionState(bool isTranscribing)
    {
        _toggleItem.Text = isTranscribing
            ? "Stop transcription"
            : "Start transcription";
        _notifyIcon.Text = isTranscribing
            ? "WindowsTranscriber - listening"
            : "WindowsTranscriber";
    }

    public void ShowNotification(string title, string message)
    {
        if (!_notificationsEnabled)
        {
            return;
        }

        _notifyIcon.Visible = true;
        _notifyIcon.BalloonTipTitle = LimitLength(title, 63);
        _notifyIcon.BalloonTipText = LimitLength(message, 255);
        _notifyIcon.BalloonTipIcon = Forms.ToolTipIcon.Info;
        _notifyIcon.ShowBalloonTip(4_000);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }

    private static string LimitLength(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];
}

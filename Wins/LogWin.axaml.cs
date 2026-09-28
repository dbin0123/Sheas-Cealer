using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sheas_Cealer_Nix.Consts;
using Sheas_Cealer_Nix.Utils;
using System;
using System.Linq;

namespace Sheas_Cealer_Nix.Wins;

public partial class LogWin : Window
{
    private const int PollIntervalMs = 500;

    private long _lastLength;
    private ScrollViewer? _scrollViewer;
    private readonly DispatcherTimer _pollTimer;

    public LogWin()
    {
        InitializeComponent();

        LoadTail();

        _pollTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(PollIntervalMs), DispatcherPriority.Background, Poll);
    }

    private void LoadTail()
    {
        string tail = LogTail.ReadTail(MainConst.AgentLogPath);

        _lastLength = LogTail.GetCurrentLength(MainConst.AgentLogPath);
        LogTextBox.Text = tail.Length > 0 ? tail : MainConst.LogWinEmptyContent;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        _scrollViewer = LogTextBox.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

        ScrollToBottom();
    }

    private void Poll(object? sender, EventArgs e)
    {
        long previousLength = _lastLength;
        string? chunk = LogTail.ReadIncrement(MainConst.AgentLogPath, ref _lastLength);

        if (string.IsNullOrEmpty(chunk))
            return;

        // agent 侧截半（或重启重写）后文件变短，返回的是全量尾部，要整体替换而不是拼接
        bool rewritten = previousLength > _lastLength;
        bool wasPlaceholder = LogTextBox.Text == MainConst.LogWinEmptyContent;
        bool wasAtBottom = IsScrolledToBottom();

        LogTextBox.Text = rewritten || wasPlaceholder ? chunk : LogTextBox.Text + chunk;

        // 只有本来就停在底部才跟着滚，用户回看历史时不要拽走
        if (wasAtBottom || wasPlaceholder)
            ScrollToBottom();
    }

    private void ScrollToBottom()
    {
        LogTextBox.CaretIndex = LogTextBox.Text?.Length ?? 0;
        _scrollViewer?.ScrollToEnd();
    }

    private bool IsScrolledToBottom() =>
        _scrollViewer is null || _scrollViewer.Offset.Y + _scrollViewer.Viewport.Height >= _scrollViewer.Extent.Height - 1;

    protected override void OnClosed(EventArgs e)
    {
        _pollTimer.Stop();

        base.OnClosed(e);
    }
}

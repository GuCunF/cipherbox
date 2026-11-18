using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CBox.Core;

namespace CBox.App;

/// <summary>
/// 只读查看窗口：边解密边显示，明文不写磁盘。
///
/// 支持两类内容：
///   图片（按魔数识别）→ 解码成位图直接显示
///   文本 → 按 1 MB 分段读取，每页 2000 行翻页显示
/// </summary>
public partial class ViewerWindow : Window
{
    private const int LoadStepBytes = 1 << 20;      // 文本每次读取 1 MB
    private const int PageLines = 2000;             // 每页 2000 行
    private const long ImageConfirmBytes = 50L << 20; // 超过 50 MB 的图片先询问

    private readonly string _path;
    private readonly string _password;
    private readonly CancellationTokenSource _cts = new();
    private readonly MemoryStream _buffer = new();

    private CbxDecryptReader? _reader;
    private string[] _lines = Array.Empty<string>();
    private int _lineStart;
    private int _pageLineCount;
    private bool _finished;
    private bool _binary;
    private bool _imageMode;
    private bool _loading;

    private readonly Stopwatch _loadStopwatch = new();
    private DispatcherTimer? _feedbackTimer;
    private bool _cancelRequested;

    private BitmapImage? _bitmap;
    private double _zoom = 1.0;
    private bool _syncingZoom;

    // 编辑状态
    private const long EditMaxBytes = 10L << 20;
    private bool _editing;
    private int _codePage;
    private bool _hasBom;
    private CbxProfile? _editProfile;

    // 进入编辑时记下文件状态，保存前对比——防止把别人的修改悄悄覆盖掉
    private DateTime _sourceWriteTimeUtc;
    private long _sourceLength;

    public ViewerWindow(string path, string password)
    {
        InitializeComponent();

        _path = path;
        _password = password;
        Title = UiText.Get("view.window.title", Path.GetFileName(path));
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadContentAsync();
    }

    /// <summary>打开（或重新打开）文件并显示。保存后也走这里刷新。</summary>
    private async Task LoadContentAsync()
    {
        try
        {
            CbxLog.Info($"开始只读查看：{CbxLog.SafePath(_path)}");
            _reader = Cbx.OpenReader(_path, Encoding.UTF8.GetBytes(_password));

            TitleText.Text = _reader.OriginalName ?? Path.GetFileName(_path);
            MetaText.Text = UiText.Get("view.meta", _reader.PlainSize, _reader.ChunkCount, _reader.ChunkSize);

            await ReadMoreBytesAsync();

            var kind = CbxFileType.Detect(_buffer.ToArray(), _reader.OriginalName);

            if (kind.IsImage)
            {
                await ShowImageAsync(kind.DisplayName);
                return;
            }

            RebuildLines();

            if (_binary)
            {
                ShowReject(kind);
                return;
            }

            RenderPage();
            EditButton.Visibility = Visibility.Visible;
        }
        catch (CbxException ex)
        {
            CbxLog.Warn($"只读查看失败：{ex.Message}");
            ShowMessage(ex.Message);
            ShowErrorDialog(ex.Message);
        }
        catch (Exception ex)
        {
            CbxLog.Error("只读查看异常", ex);
            string text = UiText.Get("view.error.open", ex.Message);
            ShowMessage(text);
            ShowErrorDialog(text);
        }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _cts.Cancel();
        _reader?.Dispose();
        _reader = null;
        _buffer.Dispose();
    }

    private async void More_Click(object sender, RoutedEventArgs e)
    {
        await NextPageAsync();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    // ---------------- 图片 ----------------

    private async Task ShowImageAsync(string kind)
    {
        if (_reader is null)
        {
            return;
        }

        if (_reader.PlainSize > ImageConfirmBytes)
        {
            var answer = AppDialog.Confirm(UiText.Get("view.image.toobig.title"), UiText.Get("view.image.toobig", $"{_reader.PlainSize / 1024.0 / 1024.0:F1}"), UiText.Get("dialog.yes"), UiText.Get("dialog.no"), this);

            if (!answer)
            {
                ShowMessage(UiText.Get("view.image.cancelled"));
                return;
            }
        }

        _imageMode = true;
        MoreButton.IsEnabled = false;
        LoadProgress.Visibility = Visibility.Visible;

        try
        {
            // 图片必须完整才能解码，所以把剩下的全部读进来
            while (!_finished)
            {
                await ReadMoreBytesAsync(int.MaxValue);
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;   // 立即解码，之后可以释放流
            bitmap.StreamSource = new MemoryStream(_buffer.ToArray());
            bitmap.EndInit();
            bitmap.Freeze();

            ContentImage.Source = bitmap;
            _bitmap = bitmap;
            ContentBox.Visibility = Visibility.Collapsed;
            ImageHost.Visibility = Visibility.Visible;
            MoreButton.Visibility = Visibility.Collapsed;   // 图片没有"下一页"概念
            ZoomPanel.Visibility = Visibility.Visible;

            EncodingText.Text = kind;
            StatusText.Text = UiText.Get("view.image.info", bitmap.PixelWidth, bitmap.PixelHeight, _reader.PlainSize);

            // 等布局完成后才知道可视区域多大，再算"适应窗口"的比例
            _ = Dispatcher.BeginInvoke(new Action(FitToWindow), System.Windows.Threading.DispatcherPriority.Loaded);
        }
        catch (Exception ex)
        {
            CbxLog.Warn($"图片解码失败：{ex.Message}");
            ShowMessage(UiText.Get("view.image.failed", kind, ex.Message) + Environment.NewLine + Environment.NewLine +
                        UiText.Get("view.image.failed.hint") + Environment.NewLine +
                        UiText.Get("view.image.failed.action"));
        }
        finally
        {
            LoadProgress.Visibility = Visibility.Collapsed;
        }
    }

    // ---------------- 编辑 ----------------

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        _ = EnterEditAsync();
    }

    /// <summary>
    /// 进入编辑：再验一次口令 → 读完全部明文 → 按原编码显示为可编辑文本。
    /// </summary>
    private async Task EnterEditAsync()
    {
        if (_editing || _reader is null || _binary || _imageMode)
        {
            return;
        }

        if (_reader.PlainSize > EditMaxBytes)
        {
            AppDialog.Info(UiText.Get("view.image.toobig.title"), UiText.Get("view.edit.toobig", _reader.PlainSize) + Environment.NewLine + Environment.NewLine + UiText.Get("view.edit.toobig.hint"), this);

            return;
        }

        var dialog = new PasswordDialog { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        // 能打开这个文件说明口令是对的，这里只是二次确认
        if (!string.Equals(dialog.Password, _password, StringComparison.Ordinal))
        {
            CbxLog.Warn("编辑前口令校验失败");
            ShowErrorDialog(UiText.Get("view.edit.wrongpass"));
            return;
        }

        EditButton.IsEnabled = false;

        try
        {
            while (!_finished)
            {
                await ReadMoreBytesAsync(int.MaxValue);
            }

            var decoded = CbxTextDecoder.Decode(_buffer.ToArray(), truncated: false);
            if (decoded.IsBinary)
            {
                ShowErrorDialog(UiText.Get("view.edit.binary"));
                return;
            }

            _codePage = decoded.CodePage;
            _hasBom = decoded.HasBom;

            // 保存时必须沿用原文件的参数，不能因为编辑就把强度降下来
            _editProfile = new CbxProfile
            {
                Name = "原文件参数",
                AlgorithmId = _reader.Header.AlgId,
                KdfId = _reader.Header.KdfId,
                Iterations = _reader.Header.KdfIterations,
                ChunkSize = (int)_reader.Header.ChunkSize,
            };

            _editing = true;

            var sourceInfo = new FileInfo(_path);
            _sourceWriteTimeUtc = sourceInfo.LastWriteTimeUtc;
            _sourceLength = sourceInfo.Length;

            ContentBox.IsReadOnly = false;
            ContentBox.Text = decoded.Text ?? string.Empty;

            EditButton.Visibility = Visibility.Collapsed;
            MoreButton.Visibility = Visibility.Collapsed;
            SaveButton.Visibility = Visibility.Visible;
            CancelEditButton.Visibility = Visibility.Visible;

            EncodingText.Text = UiText.Get("view.encoding", decoded.EncodingName ?? "-");
            StatusText.Text = UiText.Get("view.edit.hint", _editProfile?.ToString() ?? string.Empty);

            CbxLog.Info($"进入编辑：{CbxLog.SafePath(_path)}（编码 {decoded.EncodingName}，参数 {_editProfile}）");
        }
        catch (Exception ex)
        {
            CbxLog.Error("进入编辑失败", ex);
            ShowErrorDialog(UiText.Get("view.edit.enterfailed", ex.Message));
        }
        finally
        {
            EditButton.IsEnabled = true;
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!_editing || _editProfile is null || _reader is null)
        {
            return;
        }

        SaveButton.IsEnabled = false;
        string cipherTemp = _path + "." + Guid.NewGuid().ToString("N")[..8] + ".new";
        string backup = _path + "." + Guid.NewGuid().ToString("N")[..8] + ".bak";

        try
        {
            string originalName = _reader.OriginalName ?? Path.GetFileName(_path);

            // 编辑期间文件被外部改过（别的程序、另一台设备同步）就别静默覆盖
            var current = new FileInfo(_path);
            if (current.Exists &&
                (current.LastWriteTimeUtc != _sourceWriteTimeUtc || current.Length != _sourceLength))
            {
                CbxLog.Warn($"保存前检测到文件已变化：{CbxLog.SafePath(_path)}");

                bool answer = AppDialog.Confirm(UiText.Get("view.conflict.title"), UiText.Get("view.conflict") + Environment.NewLine + Environment.NewLine + UiText.Get("view.conflict.hint"), UiText.Get("dialog.yes"), UiText.Get("dialog.no"), this);

                if (!answer)
                {
                    CbxLog.Info("用户取消覆盖");
                    return;
                }
            }

            // 编码必须与打开时一致，否则 GBK 文件一保存就变乱码
            byte[] plain = CbxTextDecoder.EncodeForSave(ContentBox.Text, _codePage, _hasBom);
            var profile = _editProfile;
            var passwordBytes = Encoding.UTF8.GetBytes(_password);

            CbxLog.Info($"保存编辑：明文 {plain.Length:N0} 字节，参数 {profile}");

            await Task.Run(() => Cbx.EncryptBytes(plain, cipherTemp, passwordBytes, profile, originalName));

            // 先关掉原文件句柄，否则替换会被占用挡住
            _reader.Dispose();
            _reader = null;

            // 原子替换：要么是旧文件，要么是新文件，不存在"写了一半"
            File.Replace(cipherTemp, _path, backup, ignoreMetadataErrors: true);
            TryDeleteFile(backup);

            _editing = false;
            SaveButton.Visibility = Visibility.Collapsed;
            CancelEditButton.Visibility = Visibility.Collapsed;
            ContentBox.IsReadOnly = true;

            CbxLog.Info($"保存成功：{CbxLog.SafePath(_path)}");
            SoundFeedback.PlaySuccess();

            AppDialog.Info(UiText.Get("view.saved.title"), UiText.Get("view.saved"), this);

            await ReloadAsync();
        }
        catch (Exception ex)
        {
            CbxLog.Error("保存编辑失败", ex);
            ShowErrorDialog(UiText.Get("view.save.failed", ex.Message) + Environment.NewLine + Environment.NewLine + UiText.Get("view.save.failed.hint"));
        }
        finally
        {
            TryDeleteFile(cipherTemp);
            SaveButton.IsEnabled = true;
        }
    }

    private async void CancelEdit_Click(object sender, RoutedEventArgs e)
    {
        _editing = false;
        SaveButton.Visibility = Visibility.Collapsed;
        CancelEditButton.Visibility = Visibility.Collapsed;
        ContentBox.IsReadOnly = true;

        await ReloadAsync();
    }

    /// <summary>重新读一遍文件并显示（保存或取消编辑之后）。</summary>
    private async Task ReloadAsync()
    {
        _reader?.Dispose();
        _reader = null;

        _buffer.SetLength(0);
        _lines = Array.Empty<string>();
        _lineStart = 0;
        _pageLineCount = 0;
        _finished = false;
        _binary = false;
        _imageMode = false;

        ContentBox.Visibility = Visibility.Visible;
        ContentBox.Text = string.Empty;
        ImageHost.Visibility = Visibility.Collapsed;
        ExportButton.Visibility = Visibility.Collapsed;
        EditButton.Visibility = Visibility.Collapsed;
        MoreButton.Visibility = Visibility.Visible;
        MoreButton.IsEnabled = false;

        await LoadContentAsync();
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 删不掉就留着，不影响主流程
        }
    }

    // ---------------- 缩放 ----------------

    private const double MinZoom = 0.10;
    private const double MaxZoom = 4.00;

    /// <summary>
    /// 滚轮用的预设档位：取值来自常见的看图工具，保证每一档都是"看起来有意义的比例"。
    /// 不用固定 ±10%，否则 400% 时一格就是 40%，根本没法微调。
    /// </summary>
    private static readonly double[] ZoomSteps =
    {
        0.10, 0.125, 0.15, 0.20, 0.25, 0.333, 0.50, 0.667,
        0.75, 1.00, 1.25, 1.50, 2.00, 3.00, 4.00,
    };

    private void ImageHost_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_imageMode || _bitmap is null || Keyboard.Modifiers != ModifierKeys.Control)
        {
            return;
        }

        int index = NearestZoomStepIndex(_zoom);
        index += e.Delta > 0 ? 1 : -1;
        index = Math.Clamp(index, 0, ZoomSteps.Length - 1);

        SetZoom(ZoomSteps[index]);
        e.Handled = true;   // 缩放时不让 ScrollViewer 跟着滚
    }

    private static int NearestZoomStepIndex(double zoom)
    {
        int best = 0;
        double bestDistance = double.MaxValue;

        for (int i = 0; i < ZoomSteps.Length; i++)
        {
            double distance = Math.Abs(Math.Log(ZoomSteps[i]) - Math.Log(zoom));
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }

    private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingZoom || !_imageMode || _bitmap is null)
        {
            return;
        }

        SetZoom(ZoomFromSlider(e.NewValue));
    }

    private void Fit_Click(object sender, RoutedEventArgs e)
    {
        FitToWindow();
    }

    private void SetZoom(double zoom)
    {
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        ApplyZoom();
    }

    private void ApplyZoom()
    {
        if (_bitmap is null)
        {
            return;
        }

        ContentImage.LayoutTransform = new ScaleTransform(_zoom, _zoom);

        _syncingZoom = true;
        ZoomSlider.Value = SliderFromZoom(_zoom);
        _syncingZoom = false;

        ZoomText.Text = $"{_zoom * 100:F0}%";
    }

    /// <summary>
    /// 滑块用对数映射：0–100 的行程对应 10%–400% 的等比变化。
    /// 线性映射会让 10%→100% 只占四分之一行程，低倍率根本调不准。
    /// </summary>
    private static double ZoomFromSlider(double sliderValue)
    {
        double ratio = Math.Log(MaxZoom / MinZoom);
        double zoom = MinZoom * Math.Exp(ratio * (sliderValue / 100.0));
        return Math.Clamp(zoom, MinZoom, MaxZoom);
    }

    private static double SliderFromZoom(double zoom)
    {
        double ratio = Math.Log(MaxZoom / MinZoom);
        double value = 100.0 * Math.Log(zoom / MinZoom) / ratio;
        return Math.Clamp(value, 0, 100);
    }

    /// <summary>按当前窗口大小算出"刚好放得下"的比例，并且不超过 100%。</summary>
    private void FitToWindow()
    {
        if (_bitmap is null)
        {
            return;
        }

        double viewportWidth = ImageScroll.ViewportWidth - 4;
        double viewportHeight = ImageScroll.ViewportHeight - 4;

        if (viewportWidth <= 10 || viewportHeight <= 10)
        {
            SetZoom(1.0);
            return;
        }

        double scale = Math.Min(
            viewportWidth / _bitmap.PixelWidth,
            viewportHeight / _bitmap.PixelHeight);

        SetZoom(Math.Min(scale, 1.0));
    }

    // ---------------- 文本 ----------------

    private async Task NextPageAsync()
    {
        if (_loading || _binary || _imageMode || _reader is null)
        {
            return;
        }

        int target = _lineStart + PageLines;

        _loading = true;
        MoreButton.IsEnabled = false;

        try
        {
            while (target >= _lines.Length && !_finished)
            {
                await ReadMoreBytesAsync();
                RebuildLines();

                if (_binary)
                {
                    return;
                }
            }

            if (target < _lines.Length)
            {
                _lineStart = target;
            }

            RenderPage();
        }
        catch (OperationCanceledException)
        {
            // 窗口已关闭
        }
        catch (CbxException ex)
        {
            CbxLog.Warn($"只读查看中断：{ex.Message}");
            ShowMessage(ex.Message);
        }
        finally
        {
            _loading = false;
            if (IsLoaded && !_binary && !_imageMode)
            {
                UpdateStatus();
            }
        }
    }

    /// <summary>读更多明文进缓冲区。maxBytes 默认 1 MB，图片模式传 int.MaxValue。</summary>
    private async Task ReadMoreBytesAsync(int maxBytes = LoadStepBytes)
    {
        if (_reader is null || _finished)
        {
            return;
        }

        BeginLoadFeedback();

        try
        {
            int read = 0;
            while (read < maxBytes && !_finished)
            {
                var block = await _reader.ReadBlockAsync(_cts.Token);
                if (block is null)
                {
                    _finished = true;
                    break;
                }

                _buffer.Write(block, 0, block.Length);
                read += block.Length;

                if (_cancelRequested)
                {
                    _cancelRequested = false;
                    StatusText.Text = UiText.Get("view.stopped", _buffer.Length);
                    break;
                }
            }
        }
        finally
        {
            EndLoadFeedback();
        }
    }

    private void BeginLoadFeedback()
    {
        _loadStopwatch.Restart();
        LoadProgress.Visibility = Visibility.Visible;
        LoadProgress.IsIndeterminate = false;
        CancelButton.Visibility = Visibility.Visible;

        _feedbackTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _feedbackTimer.Tick -= FeedbackTimer_Tick;
        _feedbackTimer.Tick += FeedbackTimer_Tick;
        _feedbackTimer.Start();
    }

    private void EndLoadFeedback()
    {
        _feedbackTimer?.Stop();
        LoadProgress.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Collapsed;
    }

    /// <summary>加载期间每 400ms 刷新一次：进度条 + 已等待时长。</summary>
    private void FeedbackTimer_Tick(object? sender, EventArgs e)
    {
        if (_reader is null)
        {
            return;
        }

        double percent = _reader.PlainSize <= 0
            ? 100
            : _reader.DeliveredBytes * 100.0 / _reader.PlainSize;

        LoadProgress.Value = Math.Clamp(percent, 0, 100);

        string note = UiText.Get("view.loading", _buffer.Length, _reader.PlainSize);

        if (_loadStopwatch.Elapsed.TotalSeconds >= 3)
        {
            // 大文件、机械盘、尤其是 OneDrive 这类云盘占位文件都会卡在这里
            note += UiText.Get("view.waiting", (int)_loadStopwatch.Elapsed.TotalSeconds);
        }

        StatusText.Text = note;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cancelRequested = true;
        StatusText.Text = UiText.Get("view.stopping");
    }

    /// <summary>把缓冲区解码成行缓存；解不出文本时只置标记，由调用方决定怎么展示。</summary>
    private void RebuildLines()
    {
        var decoded = CbxTextDecoder.Decode(_buffer.ToArray(), truncated: !_finished);

        if (decoded.IsBinary)
        {
            _binary = true;
            return;
        }

        EncodingText.Text = UiText.Get("view.encoding", decoded.EncodingName ?? "-");
        _lines = (decoded.Text ?? string.Empty).Split('\n');
    }

    /// <summary>不能预览的格式：说明它是什么、为什么不支持，并给导出入口。</summary>
    private void ShowReject(FileKind kind)
    {
        MoreButton.IsEnabled = false;
        MoreButton.Visibility = Visibility.Collapsed;
        ExportButton.Visibility = Visibility.Visible;

        // 后缀名与实际内容不符时先提醒（专业模式不提示，那里由用户自己判断）
        string warning = string.Empty;
        string originalName = _reader?.OriginalName ?? Path.GetFileName(_path);
        string? expected = CbxFileType.ExpectedCategory(originalName);

        bool mismatch = expected switch
        {
            "text" => kind.Category is not "unknown",
            "image" => !kind.IsImage,
            _ => false,
        };

        // 文件里带了「不做后缀名检查」的标记就不提示——这个标记跟着文件走，
        // 所以即使换一台机器、用一般模式打开，也不会再提醒。
        if (_reader?.Header.SkipTypeCheck == true)
        {
            mismatch = false;
        }

        if (mismatch && !AppMode.IsProfessional)
        {
            warning = UiText.Get("view.mismatch", Path.GetExtension(originalName), kind.DisplayName)
                      + Environment.NewLine + Environment.NewLine;

            CbxLog.Warn($"后缀与内容不符：{originalName} 实际为 {kind.DisplayName}");
        }

        ContentBox.Visibility = Visibility.Visible;
        ContentBox.FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI");
        ContentBox.Text =
            warning +
            UiText.Get("view.reject.heading", kind.DisplayName) + Environment.NewLine + Environment.NewLine +
            kind.Hint + Environment.NewLine + Environment.NewLine +
            UiText.Get("view.reject.export") + Environment.NewLine +
            UiText.Get("view.reject.export.warn");

        EncodingText.Text = kind.DisplayName;
        StatusText.Text = string.Empty;
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = UiText.Get("view.export.folder"),
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        ExportButton.IsEnabled = false;
        CbxLog.Info($"从查看窗口导出：{CbxLog.SafePath(_path)} → {CbxLog.SafePath(dialog.FolderName)}");

        try
        {
            var report = await Task.Run(() => Cbx.DecryptFile(
                _path, dialog.FolderName, Encoding.UTF8.GetBytes(_password)));

            StatusText.Text = UiText.Get("view.export.done.status", report.OutputPath, report.PlainSize, report.Elapsed.TotalSeconds, report.MegabytesPerSecond);

            AppDialog.Info(UiText.Get("view.export.done.title"), UiText.Get("view.export.done.msg", report.OutputPath) + Environment.NewLine + Environment.NewLine + UiText.Get("view.export.done.warn"), this);
        }
        catch (CbxException ex)
        {
            CbxLog.Warn($"导出失败：{ex.Message}");
            AppDialog.Warn(UiText.Get("view.export.failed.title"), ex.Message, this);
        }
        catch (Exception ex)
        {
            CbxLog.Error("导出异常", ex);
            AppDialog.Error(UiText.Get("view.export.failed.title"), UiText.Get("view.export.failed", ex.Message), this);
        }
        finally
        {
            ExportButton.IsEnabled = true;
        }
    }

    private void RenderPage()
    {
        if (_binary || _imageMode)
        {
            return;
        }

        if (_lines.Length == 0)
        {
            ContentBox.Text = string.Empty;
            _pageLineCount = 0;
            return;
        }

        if (_lineStart >= _lines.Length)
        {
            _lineStart = Math.Max(0, _lines.Length - PageLines);
        }

        int end = Math.Min(_lineStart + PageLines, _lines.Length);
        _pageLineCount = end - _lineStart;

        ContentBox.Text = string.Join("\n", _lines[_lineStart..end]);
        ContentBox.ScrollToHome();
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_reader is null || _binary || _imageMode)
        {
            return;
        }

        if (_lines.Length == 0)
        {
            StatusText.Text = UiText.Get("view.empty");
            return;
        }

        int end = _lineStart + _pageLineCount;
        string range = UiText.Get("view.line.range", _lineStart + 1, end);

        string loaded = _finished
            ? UiText.Get("view.allloaded", _buffer.Length)
            : UiText.Get("view.loading", _buffer.Length, _reader.PlainSize);

        StatusText.Text = $"{range} · {loaded}";

        bool canGoNext = !_finished || _lineStart + PageLines < _lines.Length;
        MoreButton.IsEnabled = canGoNext;
    }

    private void ShowMessage(string message)
    {
        ContentBox.Visibility = Visibility.Visible;
        ContentBox.FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI");
        ContentBox.Text = message;
        StatusText.Text = string.Empty;
        MoreButton.IsEnabled = false;
        MoreButton.Visibility = Visibility.Collapsed;
        ZoomPanel.Visibility = Visibility.Collapsed;
    }

    /// <summary>加载/解密失败时弹窗提示，并响一声失败音。</summary>
    private void ShowErrorDialog(string message)
    {
        SoundFeedback.PlayFailure();

        // MessageBoxImage.None：带图标会再播一次系统音，和我们的提示音叠成"双响"
        AppDialog.Error(UiText.Get("view.error.title"), message, this);
    }
}

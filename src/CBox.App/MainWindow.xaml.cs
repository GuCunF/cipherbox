using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CBox.Core;
using Localization = CBox.Core.Localization;

using Microsoft.Win32;

namespace CBox.App;

public partial class MainWindow : Window
{
    private bool _busy;
    private bool _languageReady;
    private bool _soundReady;
    private bool _statusError;
    private bool _pseudoMaximized;
    private double _restoreLeft;
    private double _restoreTop;
    private double _restoreWidth;
    private double _restoreHeight;
    private readonly List<EnvironmentIssue> _environmentIssues = new();

    public MainWindow()
    {
        InitializeComponent();

        Title = CbxVersion.Display;
        AppTitle.Text = CbxVersion.Display;

#if CBTEST
        AppTitle.Text += " · 测试构建";
#endif

#if CBTEST
        // 测试构建：日志相关的开关一律默认打开，省得每次手动点
        LogPathsCheckBox.IsChecked = true;
        LogSnapshotCheckBox.IsChecked = true;
        LogAclCheckBox.IsChecked = true;

        // 默认删除半成品；要排查时再到选项里打开「失败时保留半成品」
#endif

        // 事件必须在 InitializeComponent 之后再挂，否则 XAML 里的 IsChecked 会先触发到未赋值的字段
        LogCheckBox.Checked += LogCheckBox_Changed;
        LogCheckBox.Unchecked += LogCheckBox_Changed;

        // 先读用户保存的设置，再同步到勾选框；读不到就按默认（成品静默，测试构建默认开）
#if CBTEST
        // 调试构建：日志完全放开（含路径、文件名、堆栈、环境快照）
        CbxLog.UseDebugDefaults();
        LogCheckBox.IsChecked = true;
#else
        // 发布构建：读用户设置；没有设置就是全关（默认沉默）
        CbxLog.LoadSettings();
        LogCheckBox.IsChecked = CbxLog.Enabled;
#endif

        // 恢复上次的模式选择（默认一般模式）
        AppMode.Load();
        ProModeCheckBox.IsChecked = AppMode.IsProfessional;

        Loaded += (_, _) =>
        {
            UpdateContentWidth();
            LogLayoutMetrics();
        };
        SizeChanged += (_, _) =>
        {
            UpdateContentWidth();
            LogLayoutMetrics();
        };

        HideTestOnlyControls();
#if CBTEST
        TestFailEncCheckBox.Visibility = Visibility.Visible;
        TestFailDecCheckBox.Visibility = Visibility.Visible;
#endif
        InitializeAlgCombo();
        InitializeSoundPreset();
        InitializeLanguageCombo();
        UpdateStrengthHint();
        UpdateArchiveLevelVisibility();

        SetStatus(UiText.Get("status.ready"));
        CbxLog.Info($"界面已加载，日志目录={CbxLog.LogDirectory}");
        CbxLog.Snapshot();

        string preferred = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CipherBox", "logs");

        if (!string.Equals(Path.GetFullPath(CbxLog.LogDirectory), Path.GetFullPath(preferred), StringComparison.OrdinalIgnoreCase))
        {
            SetStatus(UiText.Get("error.logdir.fallback", CbxLog.LogDirectory));
        }

        RunEnvironmentCheck();
    }

    /// <summary>
    /// 选完目标路径就探测能否写入。等到点了「开始加密」才失败，用户会以为是程序坏了。
    /// 真正写文件之前先探一下，把问题提前暴露。
    /// </summary>
    // ---------------- 自绘标题栏 ----------------

    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void MinButton_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void MaxButton_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize()
    {
        // 无边框窗口 + AllowTransparency 是全窗软件渲染，切换时最耗的是「多次重排」。
        // 这里用 SuspendLayout 的思路：先把内容区可视化树停掉更新，等尺寸改完再恢复。
        bool full = !_pseudoMaximized;

        if (ContentHost is not null)
        {
            ContentHost.Visibility = Visibility.Hidden;
        }

        if (_pseudoMaximized)
        {
            Left = _restoreLeft;
            Top = _restoreTop;
            Width = _restoreWidth;
            Height = _restoreHeight;
            _pseudoMaximized = false;
        }
        else
        {
            _restoreLeft = Left;
            _restoreTop = Top;
            _restoreWidth = Width;
            _restoreHeight = Height;

            Rect work = SystemParameters.WorkArea;
            Left = work.Left;
            Top = work.Top;
            Width = work.Width;
            Height = work.Height;
            _pseudoMaximized = true;
        }

        ApplyChromeState();

        if (ContentHost is not null)
        {
            Dispatcher.BeginInvoke(new Action(() => ContentHost.Visibility = Visibility.Visible),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    /// <summary>
    /// 无边框窗口最大化会溢出屏幕几个像素，用 Margin 补偿；
    /// 顺便切换最大化/还原的图标。
    /// </summary>
    private void MainWindow_StateChanged(object sender, EventArgs e) => ApplyChromeState();

    /// <summary>
    /// 内容宽度按窗口宽度的比例走，而不是写死像素——
    /// 这样在 1080p、4K、带鱼屏上占屏比例一致，不会一边撑满一边只剩中间一条。
    ///
    /// 比例的上下限：
    ///   下限 880 —— 窗口很小时也不至于把输入框压得太窄
    ///   上限 2200 —— 超宽屏上不再无限拉长（一行文字太长反而难读）
    /// </summary>
    private void UpdateContentWidth()
    {
        if (ContentHost is null || RootGrid is null)
        {
            return;
        }

        // 去掉 ContentHost 自己的左右 Margin
        double available = RootGrid.ActualWidth - ContentHost.Margin.Left - ContentHost.Margin.Right;

        if (available <= 0)
        {
            return;
        }

        const double Ratio = 0.70;
        const double MinWidth = 880;
        const double MaxWidth = 2200;

        double target = Math.Clamp(available * Ratio, MinWidth, MaxWidth);
        ContentHost.Width = target;

        CbxLog.Info($"布局：窗口可用 {available:F0}，内容宽度 {target:F0}（比例 {Ratio:P0}）");
    }
    /// <summary>诊断：记录窗口与内容区的实际尺寸，用来确认布局约束。</summary>
    private void LogLayoutMetrics()
    {
        try
        {
            CbxLog.Info($"诊断：窗口尺寸 {ActualWidth:F0}×{ActualHeight:F0}，" +
                        $"内容区 {ContentHost?.ActualWidth:F0}×{ContentHost?.ActualHeight:F0}，" +
                        $"系统缩放 {PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11:F2}");
        }
        catch
        {
            // 诊断失败不影响使用
        }
    }

    /// <summary>按当前是"贴满工作区"还是"普通窗口"，调整圆角、留白与图标。</summary>
    private void ApplyChromeState()
    {
        if (ChromeBorder is null)
        {
            return;
        }

        bool full = _pseudoMaximized;

        ChromeBorder.Margin = full ? new Thickness(0) : new Thickness(8);
        ChromeBorder.CornerRadius = full ? new CornerRadius(0) : new CornerRadius(10);

        if (CaptionBar is not null)
        {
            CaptionBar.CornerRadius = full ? new CornerRadius(0) : new CornerRadius(10, 10, 0, 0);
        }

        if (MaxGlyph is not null)
        {
            MaxGlyph.Data = Geometry.Parse(full
                ? "M2.5,0.5 L10.5,0.5 L10.5,8.5 M0.5,2.5 L8.5,2.5 L8.5,10.5 L0.5,10.5 Z"
                : "M0.5,0.5 L10.5,0.5 L10.5,10.5 L0.5,10.5 Z");
        }

        if (ContentHost is not null)
        {
            ContentHost.Margin = full ? new Thickness(20, 14, 20, 10) : new Thickness(18);
        }

    }
    private void WarnIfTargetNotWritable(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return;
        }

        string? directory;
        try
        {
            directory = Path.GetDirectoryName(targetPath);
        }
        catch
        {
            return;
        }

        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return;   // 目录不存在的情况交给后续流程去报错
        }

        try
        {
            string probe = Path.Combine(directory, $".cbox_probe_{Guid.NewGuid():N}.tmp");
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(0);
            }

            File.Delete(probe);
        }
        catch (Exception ex)
        {
            CbxLog.Warn($"目标目录写入探测失败：{directory} - {ex.Message}");
            SetStatus(UiText.Get("status.target.notwritable", directory), error: true);
        }
    }

    /// <summary>启动时的环境自检：静默执行，只有发现问题才在界面上露出来。</summary>
    private void RunEnvironmentCheck()
    {
        try
        {
            _environmentIssues.AddRange(CbxEnvironmentProbe.Run());
        }
        catch (Exception ex)
        {
            CbxLog.Warn($"环境自检本身出错：{ex.Message}");
            return;
        }

        if (_environmentIssues.Count == 0)
        {
            return;
        }

        EnvButton.Visibility = Visibility.Visible;
        SetStatus(UiText.Get("env.status", _environmentIssues.Count));

        CbxLog.Warn($"环境自检：发现 {_environmentIssues.Count} 项问题");
        foreach (var issue in _environmentIssues)
        {
            CbxLog.Warn($"  [{issue.Area}] {issue.Summary} | {issue.Technical.Replace(Environment.NewLine, " ")}");
        }

        // 启动就弹一次窗：说明短、说人话；技术细节留给「环境问题」按钮
        var lines = new List<string>
        {
            UiText.Get("env.heading"),
            string.Empty,
        };

        foreach (var issue in _environmentIssues)
        {
            lines.Add($"• {issue.Summary}");
            lines.Add(UiText.Get("env.advice", issue.Advice));
        }

        lines.Add(string.Empty);
        lines.Add(UiText.Get("env.detail.hint"));

        AppDialog.Warn(UiText.Get("env.title"), string.Join(Environment.NewLine, lines), this);
    }

    private void EnvButton_Click(object sender, RoutedEventArgs e)
    {
        var lines = new List<string>();

        foreach (var issue in _environmentIssues)
        {
            lines.Add($"【{issue.Area}】");
            lines.Add(issue.Technical);
            lines.Add(string.Empty);
        }

        AppDialog.Warn(UiText.Get("env.detail.title"), string.Join(Environment.NewLine, lines).TrimEnd(), this);
    }

    // ---------------- 诊断日志 ----------------

    private void LogCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        CbxLog.Enabled = LogCheckBox.IsChecked == true;
        CbxLog.Info($"诊断日志{(CbxLog.Enabled ? "已开启" : "已关闭")}");

        // 只持久化非敏感字段
        CbxLog.SaveSettings();
    }

    /// <summary>测试用：每完成 10% 暂停 0.5 秒，方便观察进度条。</summary>
    private void SlowCheckBox_Changed(object sender, RoutedEventArgs e)
    {
#if CBTEST
        CbxTestHooks.SlowMode = (sender as CheckBox)?.IsChecked == true;
#if CBTEST
        CbxLog.Info($"模拟慢速：{(CbxTestHooks.SlowMode ? "开" : "关")}");
#endif

        // 必须给可见反馈：否则用户根本判断不了这个开关有没有生效
        SetStatus(UiText.Get("status.slow", CbxTestHooks.SlowMode ? UiText.Get("common.on") : UiText.Get("common.off")));
#endif
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(CbxLog.LogDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = CbxLog.LogDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            SetStatus(UiText.Get("error.logdir.open", ex.Message), error: true);
        }
    }

    // ---------------- 浏览 ----------------

    private void EncInputBrowse_Click(object sender, RoutedEventArgs e)
    {
        if (EncModeFolder?.IsChecked == true)
        {
            var folderDialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = UiText.Get("enc.pick.folder"),
            };

            if (folderDialog.ShowDialog(this) == true)
            {
                string folder = folderDialog.FolderName.TrimEnd(Path.DirectorySeparatorChar);
                EncInputBox.Text = folder;
                EncOutputBox.Text = folder + ".cbx";
            }

            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = UiText.Get("enc.browse"),
            Filter = SafeFilter(UiText.Get("enc.filter")),
        };

        if (dialog.ShowDialog(this) == true)
        {
            ApplyEncryptInput(dialog.FileName);
        }
    }

    private void EncOutputBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = UiText.Get("enc.saveas"),
            Filter = SafeFilter("CipherBox (*.cbx)|*.cbx|" + UiText.Get("common.allfiles")),
            FileName = SuggestCbxName(EncInputBox.Text),
        };

        if (dialog.ShowDialog(this) == true)
        {
            EncOutputBox.Text = dialog.FileName;
            WarnIfTargetNotWritable(dialog.FileName);
        }
    }

    /// <summary>只有选中归档文件时才显示「查看内容」按钮。</summary>
    private void RefreshPreviewButton()
    {
        if (PreviewButton is null)
        {
            return;
        }

        bool show = false;
        string input = DecInputBox?.Text.Trim() ?? string.Empty;

        if (input.Length > 0 && File.Exists(input))
        {
            try
            {
                var header = Cbx.ReadHeader(input);
                show = header.IsArchive && header.IsPreviewable;
            }
            catch
            {
                show = false;
            }
        }

        PreviewButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DecInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshPreviewButton();
    }

    private void DecInputBrowse_Click(object sender, RoutedEventArgs e)
    {
        // 文件夹模式下必须选文件夹，否则用户根本选不中要解密的目录
        if (DecModeFolder?.IsChecked == true)
        {
            var folderDialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = UiText.Get("dec.pick.folder"),
            };

            if (folderDialog.ShowDialog(this) == true)
            {
                DecInputBox.Text = folderDialog.FolderName.TrimEnd(Path.DirectorySeparatorChar);
            }

            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = UiText.Get("dec.browse"),
            Filter = SafeFilter(UiText.Get("dec.filter")),
        };

        if (dialog.ShowDialog(this) == true)
        {
            ApplyDecryptInput(dialog.FileName);
        }
    }

    private void DecOutputBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = UiText.Get("view.export.folder"),
        };

        if (dialog.ShowDialog(this) == true)
        {
            DecOutputBox.Text = dialog.FolderName;
            WarnIfTargetNotWritable(Path.Combine(dialog.FolderName, "probe.cbx"));
        }
    }

    // ---------------- 加密 ----------------

    private async void EncStart_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        if (EncModeFolder?.IsChecked == true)
        {
            await StartFolderEncrypt();
            return;
        }

        string input = EncInputBox.Text.Trim();
        string output = EncOutputBox.Text.Trim();
        string password = EncPassword.Password;
        string confirm = EncPassword2.Password;

        if (input.Length == 0 || !File.Exists(input))
        {
            SetStatus(UiText.Get("error.pick.input"), error: true);
            return;
        }

        if (output.Length == 0)
        {
            SetStatus(UiText.Get("error.pick.output"), error: true);
            return;
        }

        if (password.Length == 0)
        {
            SetStatus(UiText.Get("error.password.empty"), error: true);
            return;
        }

        if (password != confirm)
        {
            SetStatus(UiText.Get("error.password.mismatch"), error: true);
            return;
        }

        string? directory = Path.GetDirectoryName(output);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            SetStatus(UiText.Get("error.output.missing", directory), error: true);
            return;
        }

        // 默认输出名就是「原文件名.cbx」，重复加密同一文件会静默覆盖上一次的结果。
        // 万一两次口令不同，旧密文就再也解不开了，所以必须先问。
        if (File.Exists(output))
        {
            var dialog = new OverwriteDialog(output) { Owner = this };
            bool confirmed = dialog.ShowDialog() == true;

            if (!confirmed || dialog.Choice == OverwriteChoice.Cancel)
            {
                SetStatus(UiText.Get("status.encrypt.cancelled"));
                return;
            }

            if (dialog.Choice == OverwriteChoice.SaveAsNew)
            {
                output = MakeUniquePath(output);
                EncOutputBox.Text = output;
                SetStatus(UiText.Get("status.renamed", Path.GetFileName(output)));
            }
        }

        Begin(string.Empty);
        ScrubPassword(EncPassword);
        ScrubPassword(EncPassword2);
        try
        {
            var progress = new Progress<double>(p =>
            {
                EncProgress.Value = p * 100;
                EncProgressText.Text = $"{p * 100:F0}%";
        EncProgressText.Foreground = System.Windows.Media.Brushes.White;
                SetStatus(UiText.Get("status.encrypting", $"{p * 100:F0}"));
            });

            var profile = BuildEncryptProfile();

            CbxLog.Info($"加密参数：{profile}");

            var report = await Task.Run(() => Cbx.EncryptFile(
                input, output, Encoding.UTF8.GetBytes(password),
                profile, progress));

            EncProgress.Value = 100;
            EncProgressText.Text = "100%";
        EncProgressText.Foreground = System.Windows.Media.Brushes.White;
        if (EncProgress is not null) { EncProgress.Foreground = (System.Windows.Media.Brush)FindResource("BrushSuccess"); }

            if (!SoundFeedback.PlaySuccess(report.Elapsed))
            {
                FlashStatusBar();
            }
            SetStatus(UiText.Get(
                "status.encrypt.done",
                report.OutputPath,
                report.PlainSize,
                report.CipherSize,
                report.ChunkCount,
                report.Elapsed.TotalSeconds,
                report.MegabytesPerSecond,
                report.Iterations));
        }
        catch (CbxException ex)
        {
            string message = DescribeError(ex);
            SetStatus(message, error: true);
            ShowErrorDialog(UiText.Get("error.encrypt.title"), message);
        }
        catch (Exception ex)
        {
            string text = UiText.Get("error.generic", ex.GetType().Name, ex.Message);
            SetStatus(text, error: true);
            ShowErrorDialog(UiText.Get("error.encrypt.title"), text);
        }
        finally
        {
            End();
        }
    }

    // ---------------- 解密 ----------------

    private async void DecStart_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        if (DecModeFolder?.IsChecked == true)
        {
            await StartFolderDecrypt();
            return;
        }

        string input = DecInputBox.Text.Trim();
        string output = DecOutputBox.Text.Trim();
        string password = DecPassword.Password;

        if (input.Length == 0 || !File.Exists(input))
        {
            SetStatus(UiText.Get("error.pick.cbx"), error: true);
            return;
        }

        if (password.Length == 0)
        {
            SetStatus(UiText.Get("error.password.empty"), error: true);
            return;
        }

        if (output.Length == 0)
        {
            output = Path.GetDirectoryName(input) ?? ".";
            DecOutputBox.Text = output;
        }

        Begin(string.Empty);
        ScrubPassword(DecPassword);
        try
        {
            var progress = new Progress<double>(p =>
            {
                DecProgress.Value = p * 100;
                DecProgressText.Text = $"{p * 100:F0}%";
        DecProgressText.Foreground = System.Windows.Media.Brushes.White;
                SetStatus(UiText.Get("status.decrypting", $"{p * 100:F0}"));
            });

            string overrideName = DecNameBox?.Text.Trim() ?? string.Empty;
            string? outputName = overrideName.Length == 0 ? null : Cbx.MakeSafeFileName(overrideName);

            var header = Cbx.ReadHeader(input);

            // 归档（文件夹加密的产物）要整包解开，不能当成普通文件解密。
            // 并且必须建一个父文件夹，否则条目会直接散落在输出目录里。
            if (header.IsArchive)
            {
                string folderName = overrideName.Length > 0
                    ? Cbx.MakeSafeFileName(overrideName)
                    : Path.GetFileNameWithoutExtension(input);

                string target = Path.Combine(output, folderName);

                // 目标已存在就新建「名字 (1)」，绝不把两批文件混进同一个文件夹
                if (Directory.Exists(target) || File.Exists(target))
                {
                    target = MakeUniqueDirectory(target);
                }

                await ExtractArchiveAsync(input, target, password);
                return;
            }

            // 单文件：只写了名字没写扩展名时，补上文件里记录的原扩展名
            if (outputName is not null && !Path.HasExtension(outputName))
            {
                string original = header.OriginalName ?? string.Empty;
                string extension = Path.GetExtension(original);

                if (!string.IsNullOrEmpty(extension))
                {
                    outputName += extension;
                }
            }

            var report = await Task.Run(() => Cbx.DecryptFile(
                input, output, Encoding.UTF8.GetBytes(password), outputName, progress));

            DecProgress.Value = 100;
            DecProgressText.Text = "100%";
        DecProgressText.Foreground = System.Windows.Media.Brushes.White;
        if (DecProgress is not null) { DecProgress.Foreground = (System.Windows.Media.Brush)FindResource("BrushSuccess"); }

            if (!SoundFeedback.PlaySuccess(report.Elapsed))
            {
                FlashStatusBar();
            }
            SetStatus(UiText.Get(
                "status.decrypt.done",
                report.OutputPath,
                report.PlainSize,
                report.ChunkCount,
                report.Elapsed.TotalSeconds,
                report.MegabytesPerSecond));
        }
        catch (CbxException ex)
        {
            string message = DescribeError(ex);
            SetStatus(message, error: true);
            ShowErrorDialog(UiText.Get("error.decrypt.title"), message);
        }
        catch (Exception ex)
        {
            string text = UiText.Get("error.generic", ex.GetType().Name, ex.Message);
            SetStatus(text, error: true);
            ShowErrorDialog(UiText.Get("error.decrypt.title"), text);
        }
        finally
        {
            End();
        }
    }

    // ---------------- 只读查看 ----------------

    private void DecView_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        string input = DecInputBox.Text.Trim();
        if (input.Length == 0 || !File.Exists(input))
        {
            SetStatus(UiText.Get("error.pick.cbx"), error: true);
            return;
        }

        string password = DecPassword.Password;
        if (password.Length == 0)
        {
            SetStatus(UiText.Get("error.password.empty"), error: true);
            return;
        }

        ScrubPassword(DecPassword);

        try
        {
            var viewer = new ViewerWindow(input, password) { Owner = this };
            viewer.ShowDialog();
        }
        catch (Exception ex)
        {
            CbxLog.Error("打开查看窗口失败", ex);
            SetStatus(UiText.Get("error.open.failed", ex.Message), error: true);
        }
    }

    // ---------------- 拖放 ----------------

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
        {
            return;
        }

        string first = files[0];
        if (Directory.Exists(first))
        {
            SetStatus(UiText.Get("error.drop.folder"), error: true);
            return;
        }

        if (Tabs.SelectedIndex == 0)
        {
            ApplyEncryptInput(first);
        }
        else
        {
            ApplyDecryptInput(first);
        }
    }

    // ---------------- 辅助 ----------------

    private void ApplyEncryptInput(string path)
    {
        EncInputBox.Text = path;
        EncOutputBox.Text = Path.Combine(
            Path.GetDirectoryName(path) ?? ".",
            Path.GetFileName(path) + ".cbx");

        CbxLog.Info($"选择加密输入：{CbxLog.SafePath(path)}（{new FileInfo(path).Length:N0} 字节）");
        SetStatus(UiText.Get("status.selected", Path.GetFileName(path), new FileInfo(path).Length));

        ApplyEncryptedFileName();

        WarnIfTargetNotWritable(EncOutputBox.Text);
        WarnIfExtensionMismatch(path);
    }

    private void ApplyDecryptInput(string path)
    {
        DecInputBox.Text = path;
        DecOutputBox.Text = Path.GetDirectoryName(path) ?? ".";

        CbxLog.Info($"选择解密输入：{CbxLog.SafePath(path)}");
        SetStatus(UiText.Get("status.selected.short", Path.GetFileName(path)));

        WarnIfTargetNotWritable(Path.Combine(DecOutputBox.Text, "probe.cbx"));
        UpdateViewerAvailability(path);
    }

    /// <summary>
    /// 只有确定能看的类型才显示「只读查看」。
    /// 一直显示这个按钮会误导——实际只能看文本和图片。
    ///
    /// 这里读的是文件头里记录的原始文件名（读头不需要口令），
    /// 按扩展名粗判；真正的类型判定在查看窗口里按内容做。
    /// </summary>
    private void UpdateViewerAvailability(string cbxPath)
    {
        try
        {
            var header = Cbx.ReadHeader(cbxPath);

            // 优先用头部记录的类别（加密时写入，不依赖文件名）；
            // 老文件没有类别，退回按原始文件名扩展名猜。
            // 类别是「其他」时不轻易暴露按钮——那可能是全加密文件的掩护值；
            // 但专业模式下允许自己试，那里由用户承担。
            bool viewable = header.HasCategory
                ? header.CategoryCode is 0 or 1 || (header.CategoryCode == 2 && AppMode.IsProfessional)
                : CbxFileType.IsLikelyViewable(header.HasName ? header.OriginalName : null);

            DecViewButton.Visibility = viewable ? Visibility.Visible : Visibility.Collapsed;

            if (!viewable)
            {
                CbxLog.Info($"不显示只读查看：类别={header.CategoryName}");
            }
        }
        catch (Exception ex)
        {
            // 头都读不出来，肯定看不了，按钮保持隐藏即可
            DecViewButton.Visibility = Visibility.Collapsed;
            CbxLog.Warn($"读取文件头失败，隐藏只读查看：{ex.Message}");
        }
    }

    /// <summary>
    /// 后缀名与实际内容不符时提醒。
    /// 专业模式不提示——那里允许用户自己决定怎么处理。
    /// </summary>
    private void WarnIfExtensionMismatch(string path)
    {
        if (AppMode.IsProfessional)
        {
            return;
        }

        try
        {
            string? expected = CbxFileType.ExpectedCategory(path);
            if (expected is null)
            {
                return;   // 扩展名没有预期，谈不上"不符"
            }

            var head = new byte[16];
            int read;

            using (var stream = File.OpenRead(path))
            {
                read = stream.Read(head, 0, head.Length);
            }

            if (read <= 0)
            {
                return;
            }

            var kind = CbxFileType.Detect(head.AsSpan(0, read), Path.GetFileName(path));

            bool mismatch = expected switch
            {
                // 文本文件真正的样子是"没有魔数"（Detect 返回 unknown）
                "text" => kind.Category is not "unknown",
                "image" => !kind.IsImage,
                _ => false,
            };

            if (!mismatch)
            {
                return;
            }

            string extension = Path.GetExtension(path);
            CbxLog.Warn($"后缀与内容不符：{CbxLog.SafePath(path)} 实际判定为 {kind.DisplayName}");

            SetStatus(UiText.Get("status.extension.mismatch", extension, kind.DisplayName), error: true);
        }
        catch (Exception ex)
        {
            CbxLog.Warn($"后缀校验失败：{ex.Message}");
        }
    }

    private static string SuggestCbxName(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return UiText.Get("common.unnamed") + ".cbx";
        }

        return Path.GetFileName(input) + ".cbx";
    }

    /// <summary>
    /// 目标已存在时生成 "xxx (1).cbx" 这样的新名字，绝不覆盖旧文件。
    /// 如果原名本身已经是 "xxx (3)"，就从 4 开始——否则会越叠越长：xxxx (1) (1) (1)
    /// </summary>
    private static string MakeUniquePath(string path)
    {
        string directory = Path.GetDirectoryName(path) ?? ".";
        string stem = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);

        int start = 1;
        var match = System.Text.RegularExpressions.Regex.Match(stem, @"^(.*) \((\d+)\)$");
        if (match.Success)
        {
            stem = match.Groups[1].Value;
            start = int.Parse(match.Groups[2].Value) + 1;
        }

        for (int i = start; i < 10000; i++)
        {
            string candidate = Path.Combine(directory, $"{stem} ({i}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new CbxException("同名文件太多了，无法生成新文件名。");
    }

    private void Begin(string message)
    {
        _busy = true;
        EncStartButton.IsEnabled = false;
        DecStartButton.IsEnabled = false;
        EncProgress.Value = 0;
        DecProgress.Value = 0;
        EncProgressText.Text = string.Empty;
        if (EncProgress is not null) { EncProgress.Foreground = (System.Windows.Media.Brush)FindResource("BrushPrimary"); }
        DecProgressText.Text = string.Empty;
        if (DecProgress is not null) { DecProgress.Foreground = (System.Windows.Media.Brush)FindResource("BrushPrimary"); }

        // 处理期间把状态栏放大加粗，否则底下一行小字很容易被忽略
        StatusText.FontSize = 18;
        StatusText.FontWeight = FontWeights.Bold;

        // 有超出默认集的日志字段时提示一次，不然用户不知道多记了什么
        if (CbxLog.HasExtraFields)
        {
            CbxLog.Info(UiText.Get("log.notice"));
        }

        if (message.Length > 0)
        {
            SetStatus(message);
        }
        else
        {
            SetStatus(UiText.Get("status.processing"));
        }
    }

    private void End()
    {
        _busy = false;
        EncStartButton.IsEnabled = true;
        DecStartButton.IsEnabled = true;

        ResetTestFailChecks();

        // 故障注入是一次性的：操作结束后自动复位，避免下次莫名其妙又失败
#if CBTEST
        {
            CbxTestHooks.FailEncryptAtPercent = -1;
            CbxTestHooks.FailDecryptAtPercent = -1;
            FaultCombo.SelectedIndex = 0;
        }
#endif

        // 出错时保持大字，别让用户错过提示
        if (!_statusError)
        {
            StatusText.FontSize = 14;
            StatusText.FontWeight = FontWeights.Normal;
        }
    }

    /// <summary>
    /// 正式构建：点下按钮后立刻清空口令框，避免口令长期停留在界面上。
    /// 测试构建：保留内容，方便用同一口令反复测试（编译开关 CBTEST）。
    /// </summary>
    private static void ScrubPassword(System.Windows.Controls.PasswordBox box)
    {
#if !CBTEST
        box.Clear();
#endif
    }

    /// <summary>测试用：人为制造一次失败，验证错误提示与临时文件清理。</summary>
    private void FaultCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
#if CBTEST
        if (sender is not ComboBox combo)
        {
            return;
        }

        CbxTestHooks.FailEncryptAtPercent = -1;
        CbxTestHooks.FailDecryptAtPercent = -1;

        switch (combo.SelectedIndex)
        {
            case 1:
                CbxTestHooks.FailEncryptAtPercent = 50;
                break;
            case 2:
                CbxTestHooks.FailDecryptAtPercent = 50;
                break;
        }

        string label = combo.SelectedIndex switch
        {
            1 => UiText.Get("fault.encrypt"),
            2 => UiText.Get("fault.decrypt"),
            _ => UiText.Get("fault.none"),
        };

        CbxLog.Info($"故障注入：{label}");
        SetStatus(UiText.Get("status.fault", label));
#endif
    }

    /// <summary>加密强度下拉框：只影响 PBKDF2 迭代次数，文件格式不变。</summary>
    private void EncStrength_Changed(object sender, SelectionChangedEventArgs e)
    {
        UpdateStrengthHint();
        UpdateArchiveLevelVisibility();
    }

    private void UpdateStrengthHint()
    {
        if (EncStrengthHint is null)
        {
            return;
        }

        // 两套说法：
        //   一般模式 → 回答"该选哪个"（使用场景）
        //   专业模式 → 回答"具体是什么参数"（算法、迭代次数、相对倍数）
        EncStrengthHint.Text = AppMode.IsProfessional
            ? DescribeStrengthForProfessional(SelectedProfile())
            : DescribeStrengthForGeneral(SelectedProfile());
    }

    private static string DescribeStrengthForGeneral(CbxProfile profile)
    {
        return profile.Name switch
        {
            "快速" => UiText.Get("strength.hint.quick"),
            "高强度" => UiText.Get("strength.hint.strong"),
            _ => UiText.Get("strength.hint.standard"),
        };
    }

    private static string DescribeStrengthForProfessional(CbxProfile profile)
    {
        return profile.Name switch
        {
            "快速" => UiText.Get("strength.hint.quick.pro", profile.Iterations),
            "高强度" => UiText.Get("strength.hint.strong.pro", profile.Iterations),
            _ => UiText.Get("strength.hint.standard.pro", profile.Iterations),
        };
    }

    private CbxProfile SelectedProfile()
    {
        return EncStrengthCombo.SelectedIndex switch
        {
            0 => CbxProfile.Quick,
            2 => CbxProfile.Strong,
            _ => CbxProfile.Standard,
        };
    }

    /// <summary>一般/专业模式切换（当前只影响文案措辞与专业选项的可见性）。</summary>
    private void ProMode_Changed(object sender, RoutedEventArgs e)
    {
        AppMode.IsProfessional = (sender as CheckBox)?.IsChecked == true;
        AppMode.Save();

        if (SkipTypeCheckBox is not null)
        {
            SkipTypeCheckBox.Visibility = AppMode.IsProfessional ? Visibility.Visible : Visibility.Collapsed;

            if (!AppMode.IsProfessional)
            {
                SkipTypeCheckBox.IsChecked = false;   // 退回一般模式时不要留下这个标记
            }
        }

        if (FullEncryptBox is not null)
        {
            FullEncryptBox.Visibility = AppMode.IsProfessional ? Visibility.Visible : Visibility.Collapsed;

            if (!AppMode.IsProfessional)
            {
                FullEncryptBox.IsChecked = false;
            }
        }

        bool pro = AppMode.IsProfessional;

#if CBTEST
        // 日志的三个敏感开关只存在于调试构建
        ToggleSensitiveCheck(LogPathsCheckBox, true, LogField.Paths);
        ToggleSensitiveCheck(LogSnapshotCheckBox, true, LogField.Snapshot);
        ToggleSensitiveCheck(LogAclCheckBox, true, LogField.Acl);
#else
        if (LogPathsCheckBox is not null) { LogPathsCheckBox.Visibility = Visibility.Collapsed; }
        if (LogSnapshotCheckBox is not null) { LogSnapshotCheckBox.Visibility = Visibility.Collapsed; }
        if (LogAclCheckBox is not null) { LogAclCheckBox.Visibility = Visibility.Collapsed; }

#endif

        // 「失败保留半成品」两种构建都有，但只在专业模式下露出，默认关
        if (KeepFailedCheckBox is not null)
        {
            KeepFailedCheckBox.Visibility = pro ? Visibility.Visible : Visibility.Collapsed;

            if (!pro && KeepFailedCheckBox.IsChecked == true)
            {
                KeepFailedCheckBox.IsChecked = false;
                CbxTestHooks.KeepFailedOutput = false;
            }
        }

        if (AlgPanel is not null)
        {
            AlgPanel.Visibility = AppMode.IsProfessional ? Visibility.Visible : Visibility.Collapsed;
        }

        if (DisplayNamePanel is not null)
        {
            DisplayNamePanel.Visibility = AppMode.IsProfessional ? Visibility.Visible : Visibility.Collapsed;

            if (!AppMode.IsProfessional && DisplayNameBox is not null)
            {
                DisplayNameBox.Text = string.Empty;
            }
        }

        UpdateStrengthHint();
        UpdateArchiveLevelVisibility();

        if (SubtitleText is not null)
        {
            SubtitleText.Text = UiText.Get("app.subtitle");
        }

        CbxLog.Info($"界面模式：{(AppMode.IsProfessional ? "专业" : "一般")}");
    }

    /// <summary>语言切换：先双语确认，再保存设置并重启。</summary>
    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_languageReady is false || LanguageCombo?.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        if (item.Tag is not string language || language == Localization.Language)
        {
            return;
        }

        // 重启会丢掉当前界面里的一切——已选的文件、输入的口令、还没开始的操作
        bool answer = AppDialog.Confirm(UiText.Get("lang.restart.title"), UiText.Get("lang.restart.body"), UiText.Get("dialog.yes"), UiText.Get("dialog.no"), this);

        if (!answer)
        {
            // 用户反悔：把下拉框选回当前语言，别让它显示一个并没有生效的选择
            _languageReady = false;
            SelectCurrentLanguage();
            _languageReady = true;
            return;
        }

        CbxLog.Info($"用户切换界面语言：{Localization.Language} → {language}");
        Localization.SaveLanguage(language);

        try
        {
            string? exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe)
                {
                    UseShellExecute = true,
                });
            }
        }
        catch (Exception ex)
        {
            CbxLog.Warn($"重启失败：{ex.Message}");
        }

        Application.Current.Shutdown();
    }

    private void SelectCurrentLanguage()
    {
        foreach (ComboBoxItem existing in LanguageCombo.Items)
        {
            if (existing.Tag is string tag && tag == Localization.Language)
            {
                LanguageCombo.SelectedItem = existing;
                return;
            }
        }
    }

    /// <summary>把可用语言填进下拉框，并选中当前语言。</summary>
    /// <summary>
    /// 发布构建里没有测试控件：模拟慢速、故障注入只存在于 CBTEST 构建。
    /// </summary>
    private void HideTestOnlyControls()
    {
#if !CBTEST
        if (SlowCheckBox is not null)
        {
            SlowCheckBox.Visibility = Visibility.Collapsed;
        }

        if (FaultCombo is not null)
        {
            FaultCombo.Visibility = Visibility.Collapsed;
        }
#endif
    }

    private void InitializeLanguageCombo()
    {
        foreach (string language in Localization.AvailableLanguages)
        {
            var item = new ComboBoxItem
            {
                Content = language switch
                {
                    "zh-CN" => UiText.Get("lang.zh"),
                    "en-US" => UiText.Get("lang.en"),
                    _ => language,
                },
                Tag = language,
            };

            LanguageCombo.Items.Add(item);

            if (language == Localization.Language)
            {
                LanguageCombo.SelectedItem = item;
            }
        }

        _languageReady = true;
    }

    /// <summary>展开/收起选项区。默认收起，避免和标题挤在一起。</summary>
    private void OptionsToggle_Click(object sender, RoutedEventArgs e)
    {
        bool show = OptionsPanel.Visibility != Visibility.Visible;
        OptionsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        OptionsToggle.Content = UiText.Get(show ? "opt.hide" : "opt.show");
    }

    /// <summary>
    /// 「加密文件名」：只改磁盘上那个 .cbx 的名字，
    /// 文件头里记录的仍是真实原名——所以解密后会恢复成原文件名。
    /// </summary>
    private void DisplayName_Changed(object sender, TextChangedEventArgs e)
    {
        ApplyEncryptedFileName();
    }

    private void ApplyEncryptedFileName()
    {
        string input = EncInputBox.Text;

        if (string.IsNullOrWhiteSpace(input) || DisplayNameBox is null)
        {
            return;
        }

        string directory = Path.GetDirectoryName(input) ?? ".";
        string raw = DisplayNameBox.Text.Trim();
        string name = Cbx.MakeSafeFileName(raw);

        // 非法字符不只是替换掉就算了：得让用户知道名字被改成了什么
        if (raw.Length > 0 && name != raw)
        {
            SetStatus(UiText.Get("status.name.invalid", name), error: true);
        }

        EncOutputBox.Text = string.IsNullOrEmpty(raw)
            ? Path.Combine(directory, Path.GetFileName(input) + ".cbx")
            : Path.Combine(directory, name + ".cbx");
    }

    /// <summary>解密输出名：留空则用文件头里记录的名字。</summary>
    private void DecName_Changed(object sender, TextChangedEventArgs e)
    {
        if (DecNameHint is null || DecNameBox is null)
        {
            return;
        }

        string raw = DecNameBox.Text.Trim();
        if (raw.Length == 0)
        {
            DecNameHint.Text = UiText.Get("dec.savename.hint");
            return;
        }

        string safe = Cbx.MakeSafeFileName(raw);
        if (safe != raw)
        {
            DecNameHint.Text = UiText.Get("status.name.invalid", safe);
            SetStatus(UiText.Get("status.name.invalid", safe), error: true);
            return;
        }

        DecNameHint.Text = string.Empty;
    }

    /// <summary>
    /// 把异常翻成给人看的文字。
    /// 数据块出错时补一行块号——比笼统的"文件已损坏"有用得多。
    /// </summary>
    private string DescribeError(CbxException ex)
    {
        if (ex.ChunkIndex is int index)
        {
            return UiText.Get("error.chunk", index + 1) + Environment.NewLine + ex.Message;
        }

        return ex.Message;
    }

    /// <summary>专业模式：是否把完整路径写进日志（敏感项，不持久化）。</summary>
    private void LogPaths_Changed(object sender, RoutedEventArgs e)
    {
        CbxLog.SetSensitive(LogField.Paths, (sender as CheckBox)?.IsChecked == true);
        CbxLog.Info($"日志完整路径：{(CbxLog.Has(LogField.Paths) ? "开" : "关")}");
    }

    /// <summary>敏感日志开关的可见性：退出专业模式时一并关掉，避免隐形状态。</summary>
    private static void ToggleSensitiveCheck(CheckBox? box, bool visible, LogField field)
    {
        if (box is null)
        {
            return;
        }

        box.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        if (!visible && box.IsChecked == true)
        {
            box.IsChecked = false;
            CbxLog.SetSensitive(field, false);
        }
    }

    private void LogSnapshot_Changed(object sender, RoutedEventArgs e)
    {
        CbxLog.SetSensitive(LogField.Snapshot, (sender as CheckBox)?.IsChecked == true);
    }

    private void LogAcl_Changed(object sender, RoutedEventArgs e)
    {
        CbxLog.SetSensitive(LogField.Acl, (sender as CheckBox)?.IsChecked == true);
    }

    /// <summary>
    /// 专业模式：失败时是否保留写了一半的文件。
    /// 默认关（失败即删除），打开后才保留，方便排查。
    /// </summary>
    private void KeepFailed_Changed(object sender, RoutedEventArgs e)
    {
        CbxTestHooks.KeepFailedOutput = (sender as CheckBox)?.IsChecked == true;
#if CBTEST
        CbxLog.Info($"失败保留半成品：{(CbxTestHooks.KeepFailedOutput ? "开" : "关")}");
#endif
    }

    /// <summary>
    /// 没发出声音时的视觉替代：状态栏短暂变绿一下。
    /// 静音档、系统静音、或者"操作太快不播成功音"都会走到这里。
    /// </summary>
    private async void FlashStatusBar()
    {
        if (StatusText is null)
        {
            return;
        }

        var original = StatusText.Foreground;
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource("BrushSuccess");

        await Task.Delay(600);

        if (!_statusError)
        {
            StatusText.Foreground = original;
        }
    }

    /// <summary>把可用的加密算法填进下拉框（专业模式可见）。</summary>
    private void InitializeAlgCombo()
    {
        foreach (var cipher in CipherRegistry.All)
        {
            var item = new ComboBoxItem
            {
                Content = $"{cipher.DisplayName} ({cipher.AlgorithmId})",
                Tag = cipher.AlgorithmId,
            };

            AlgCombo.Items.Add(item);

            if (cipher.AlgorithmId == CbxProfile.Default.AlgorithmId)
            {
                AlgCombo.SelectedItem = item;
            }
        }
    }

    /// <summary>把保存的档位选进下拉框。</summary>
    private void InitializeSoundPreset()
    {
        SoundFeedback.Load();

        foreach (ComboBoxItem item in SoundPresetBox.Items)
        {
            if (item.Tag is SoundPreset preset && preset == SoundFeedback.Preset)
            {
                SoundPresetBox.SelectedItem = item;
                break;
            }
        }

        _soundReady = true;
    }

    /// <summary>提示音档位选择。</summary>
    private void SoundPreset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_soundReady is false || SoundPresetBox?.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        if (item.Tag is SoundPreset preset)
        {
            SoundFeedback.SetPreset(preset);
            CbxLog.Info($"提示音档位：{preset}");
        }
    }

    /// <summary>
    /// 测试用：勾上之后下一次加密必定在 50% 处失败。
    /// 放在「开始加密」旁边，比藏在选项面板里显眼得多。
    /// </summary>
    private void TestFailEnc_Changed(object sender, RoutedEventArgs e)
    {
#if CBTEST
        bool armed = (sender as CheckBox)?.IsChecked == true;
        CbxTestHooks.FailEncryptAtPercent = armed ? 50 : -1;
        CbxLog.Info($"加密失败测试：{(armed ? "已布置" : "取消")}");
        SetStatus(UiText.Get("status.fault", UiText.Get("fault.encrypt")));
#endif
    }

    private void TestFailDec_Changed(object sender, RoutedEventArgs e)
    {
#if CBTEST
        bool armed = (sender as CheckBox)?.IsChecked == true;
        CbxTestHooks.FailDecryptAtPercent = armed ? 50 : -1;
        CbxLog.Info($"解密失败测试：{(armed ? "已布置" : "取消")}");
        SetStatus(UiText.Get("status.fault", UiText.Get("fault.decrypt")));
#endif
    }

    /// <summary>把两个失败测试勾选框复位（操作结束后调用）。</summary>
    private void ResetTestFailChecks()
    {
#if CBTEST
        if (TestFailEncCheckBox is not null && TestFailEncCheckBox.IsChecked == true)
        {
            TestFailEncCheckBox.IsChecked = false;
        }

        if (TestFailDecCheckBox is not null && TestFailDecCheckBox.IsChecked == true)
        {
            TestFailDecCheckBox.IsChecked = false;
        }
#endif
    }


    /// <summary>
    /// 按当前界面组装加密参数：强度档位 + 专业模式下的算法 / 全加密 / 跳过后缀检查。
    /// 单文件与批量加密共用同一份逻辑，避免两边行为不一致。
    /// </summary>
    private CbxProfile BuildEncryptProfile()
    {
        var profile = SelectedProfile();

        if (!AppMode.IsProfessional)
        {
            return profile;
        }

        bool fullEncryption = FullEncryptBox?.IsChecked == true;
        bool skipTypeCheck = SkipTypeCheckBox?.IsChecked == true;

        byte selectedAlg = AlgCombo?.SelectedItem is ComboBoxItem algItem && algItem.Tag is byte alg
            ? alg
            : profile.AlgorithmId;

        if (!fullEncryption && !skipTypeCheck && selectedAlg == profile.AlgorithmId)
        {
            return profile;
        }

        return new CbxProfile
        {
            Name = profile.Name,
            AlgorithmId = selectedAlg,
            KdfId = profile.KdfId,
            Iterations = profile.Iterations,
            ChunkSize = profile.ChunkSize,
            SkipTypeCheck = skipTypeCheck,
            EncryptName = fullEncryption,
            HideCategory = fullEncryption,
        };
    }


    /// <summary>
    /// 加密模式切换：单个文件 / 整个文件夹。
    /// 文件夹模式下输出路径由源路径自动推出（源目录名 + ".cbx"），所以隐藏输出行。
    /// </summary>
    private void EncMode_Changed(object sender, RoutedEventArgs e)
    {
        if (EncInputLabel is null || EncInputBox is null)
        {
            return;
        }

        bool folderMode = EncModeFolder?.IsChecked == true;

        EncInputLabel.Text = UiText.Get(folderMode ? "enc.input.folder" : "enc.input");
        EncOutputRow.Visibility = folderMode ? Visibility.Collapsed : Visibility.Visible;

        if (FolderModeBox is not null)
        {
            FolderModeBox.Visibility = folderMode ? Visibility.Visible : Visibility.Collapsed;
        }

        if (FolderHintBox is not null)
        {
            FolderHintBox.Visibility = folderMode ? Visibility.Visible : Visibility.Collapsed;
        }

        UpdateFolderHint();
        UpdateArchiveLevelVisibility();

        EncHint.Text = UiText.Get(folderMode ? "enc.drop.hint.folder" : "enc.drop.hint");
        EncInputBox.Text = string.Empty;
        EncOutputBox.Text = string.Empty;
        SetStatus(string.Empty);
    }

    /// <summary>
    /// 文件夹加密（方案 A：镜像结构）。
    /// 输出目录 = 源目录名 + ".cbx"，解密时去掉这个后缀就回到原名字。
    /// </summary>
    private async Task StartFolderEncrypt()
    {
        string source = EncInputBox.Text.Trim().TrimEnd(Path.DirectorySeparatorChar);
        string password = EncPassword.Password;

        if (source.Length == 0 || !Directory.Exists(source))
        {
            SetStatus(UiText.Get("error.pick.folder"), error: true);
            return;
        }

        if (password.Length == 0)
        {
            SetStatus(UiText.Get("error.password.empty"), error: true);
            return;
        }

        if (password != EncPassword2.Password)
        {
            SetStatus(UiText.Get("error.password.mismatch"), error: true);
            return;
        }

        // 目标已存在就换个名字，而不是拒绝——报错会让人以为按钮没反应
        string output = MakeUniqueDirectory(source + ".cbx");

        var profile = BuildEncryptProfile();
        var passwordBytes = Encoding.UTF8.GetBytes(password);

        var reporter = new Progress<(int Done, int Total, string Current)>(p =>
        {
            if (p.Total == 0)
            {
                return;
            }

            EncProgress.Value = p.Done * 100.0 / p.Total;
            EncProgressText.Text = $"{p.Done}/{p.Total}";
        EncProgressText.Foreground = System.Windows.Media.Brushes.White;
            SetStatus(UiText.Get("folder.running", p.Done + 1, p.Total, p.Current));
        });

        Begin(string.Empty);

        try
        {
            bool asArchive = (FolderModeBox?.SelectedItem as ComboBoxItem)?.Tag as string != "Batch";
            bool fullLevel = (ArchiveLevelBox?.SelectedItem as ComboBoxItem)?.Tag as string == "Full";

            var report = await Task.Run(() =>
            {
                if (asArchive)
                {
                    CbxArchive.Create(source, output, passwordBytes, profile, previewable: !fullLevel);
                    return new FolderReport(1, 1, 0, 0, output);
                }

                return CbxBatch.Encrypt(source, output, passwordBytes, profile, true, reporter);
            });

            EncProgress.Value = 100;
            EncProgressText.Text = "100%";
        EncProgressText.Foreground = System.Windows.Media.Brushes.White;
        if (EncProgress is not null) { EncProgress.Foreground = (System.Windows.Media.Brush)FindResource("BrushSuccess"); }
            ScrubPassword(EncPassword);
            ScrubPassword(EncPassword2);

            string summary = UiText.Get("folder.done", report.Succeeded, report.Skipped, report.Failed);

            if (report.Copied > 0)
            {
                summary += Environment.NewLine + UiText.Get("folder.copied", report.Copied);
            }

            summary += Environment.NewLine + Environment.NewLine + output;

            SetStatus(summary);
            SoundFeedback.PlaySuccess();

            if (report.Failed > 0) { AppDialog.Warn(UiText.Get("folder.title"), summary, this); } else { AppDialog.Info(UiText.Get("folder.title"), summary, this); }
        }
        catch (Exception ex)
        {
            CbxLog.Error("文件夹加密失败", ex);
            SetStatus(UiText.Get("folder.failed", ex.Message), error: true);
            ShowErrorDialog(UiText.Get("folder.title"), ex.Message);
        }
        finally
        {
            End();
        }
    }

    /// <summary>文件夹解密：把 "xxx.cbx" 目录还原成 "xxx"。</summary>
    private async Task StartFolderDecrypt()
    {
        string source = DecInputBox.Text.Trim().TrimEnd(Path.DirectorySeparatorChar);
        string password = DecPassword.Password;

        if (source.Length == 0 || !Directory.Exists(source))
        {
            SetStatus(UiText.Get("error.pick.folder"), error: true);
            return;
        }

        if (password.Length == 0)
        {
            SetStatus(UiText.Get("error.password.empty"), error: true);
            return;
        }

        string output = source.EndsWith(".cbx", StringComparison.OrdinalIgnoreCase)
            ? source[..^4]
            : source + ".out";

        // 原目录还在时自动改成 "xxx (2)"，不再直接拒绝
        output = MakeUniqueDirectory(output);

        var passwordBytes = Encoding.UTF8.GetBytes(password);

        var reporter = new Progress<(int Done, int Total, string Current)>(p =>
        {
            if (p.Total == 0)
            {
                return;
            }

            DecProgress.Value = p.Done * 100.0 / p.Total;
            DecProgressText.Text = $"{p.Done}/{p.Total}";
        DecProgressText.Foreground = System.Windows.Media.Brushes.White;
            SetStatus(UiText.Get("folder.decrypt.running", p.Done + 1, p.Total, p.Current));
        });

        Begin(string.Empty);

        try
        {
            var report = await Task.Run(() =>
            {
                if (File.Exists(source))
                {
                    var header = Cbx.ReadHeader(source);

                    if (!header.IsArchive)
                    {
                        throw new CbxException("这个文件不是归档，请用「单个文件」模式解密。");
                    }

                    ConflictChoice? globalChoice = null;
                    int count = CbxArchive.ExtractAll(source, output, passwordBytes, path =>
                    {
                        // 回到主线程弹框：ExtractAll 跑在后台线程
                        return Dispatcher.Invoke(() =>
                        {
                            if (globalChoice.HasValue)
                            {
                                if (globalChoice.Value == ConflictChoice.Cancel)
                                {
                                    throw new CbxException("已按你的选择停止解压。");
                                }

                                return globalChoice.Value == ConflictChoice.Overwrite;
                            }

                            var dialog = new ConflictDialog(path) { Owner = this };
                            dialog.ShowDialog();

                            if (dialog.ApplyToAll)
                            {
                                globalChoice = dialog.Choice;
                            }

                            if (dialog.Choice == ConflictChoice.Cancel)
                            {
                                throw new CbxException("已按你的选择停止解压。");
                            }

                            return dialog.Choice == ConflictChoice.Overwrite;
                        });
                    }, new Progress<double>(p =>
                    {
                        DecProgress.Value = p * 100;
                        DecProgressText.Text = $"{p:P0}";
        DecProgressText.Foreground = System.Windows.Media.Brushes.White;
                    }));

                    return new FolderReport(count, count, 0, 0, output, Formats: $"归档 {count} 个条目");
                }

                return CbxBatch.Decrypt(source, output, passwordBytes, true, reporter);
            });

            DecProgress.Value = 100;
            DecProgressText.Text = "100%";
        DecProgressText.Foreground = System.Windows.Media.Brushes.White;
        if (DecProgress is not null) { DecProgress.Foreground = (System.Windows.Media.Brush)FindResource("BrushSuccess"); }
            ScrubPassword(DecPassword);

            string summary = UiText.Get("folder.done", report.Succeeded, report.Skipped, report.Failed)
                             + Environment.NewLine + Environment.NewLine + output;

            SetStatus(summary);
            SoundFeedback.PlaySuccess();

            if (report.Failed > 0) { AppDialog.Warn(UiText.Get("folder.decrypt.title"), summary, this); } else { AppDialog.Info(UiText.Get("folder.decrypt.title"), summary, this); }
        }
        catch (Exception ex)
        {
            CbxLog.Error("文件夹解密失败", ex);
            SetStatus(UiText.Get("folder.failed", ex.Message), error: true);
            ShowErrorDialog(UiText.Get("folder.decrypt.title"), ex.Message);
        }
        finally
        {
            End();
        }
    }

    /// <summary>解密模式切换：单个文件 / 整个文件夹。</summary>
    private void DecMode_Changed(object sender, RoutedEventArgs e)
    {
        if (DecInputLabel is null || DecInputBox is null)
        {
            return;
        }

        bool folderMode = DecModeFolder?.IsChecked == true;

        if (PreviewButton is not null)
        {
            PreviewButton.Visibility = Visibility.Collapsed;
        }

        DecInputLabel.Text = UiText.Get(folderMode ? "dec.input.folder" : "dec.input");
        DecHint.Text = UiText.Get(folderMode ? "dec.drop.hint.folder" : "dec.output.hint");

        // 文件夹模式下输出目录与保存名都由程序推导，留着会让人以为必须填
        if (DecOutputRow is not null)
        {
            DecOutputRow.Visibility = folderMode ? Visibility.Collapsed : Visibility.Visible;
        }

        if (DecNameRow is not null)
        {
            DecNameRow.Visibility = folderMode ? Visibility.Collapsed : Visibility.Visible;
        }

        DecInputBox.Text = string.Empty;
        DecViewButton.Visibility = Visibility.Collapsed;
        SetStatus(string.Empty);
    }

    /// <summary>文件夹方案切换：镜像（保留结构）/ 扁平（隐藏结构）。</summary>
    private void FolderMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (FolderModeBox?.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            CbxLog.Info($"文件夹方案：{tag}");
        }

        // 等级只对「归档」有意义：选批量时必须藏起来
        UpdateFolderHint();
        UpdateArchiveLevelVisibility();
    }

    /// <summary>
    /// 目标文件夹已存在时生成 "xxx (2)" 这样的新名字。
    /// 直接报错会让人以为"点了没反应"，而覆盖又绝对不行。
    /// </summary>
    private static string MakeUniqueDirectory(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            return path;
        }

        // 有扩展名时要插在扩展名"前面"：
        //   04-归档大量.cbx → 04-归档大量 (1).cbx（而不是 04-归档大量.cbx (1)）
        string directory = Path.GetDirectoryName(path) ?? string.Empty;
        string name = Path.GetFileName(path);
        string extension = Path.GetExtension(name);
        bool isFile = extension.Length > 0 && !Directory.Exists(path);

        string stem = isFile ? name[..^extension.Length] : name;

        for (int i = 1; i < 1000; i++)
        {
            string candidateName = isFile ? $"{stem} ({i}){extension}" : $"{stem} ({i})";
            string candidate = directory.Length == 0
                ? candidateName
                : Path.Combine(directory, candidateName);

            if (!Directory.Exists(candidate) && !File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new CbxException("同名文件太多了，无法生成新名字。");
    }

    /// <summary>等级选择只在「归档」方案下有意义。</summary>
    private void UpdateArchiveLevelVisibility()
    {
        if (ArchiveLevelBox is null)
        {
            return;
        }

        // 等级只对「归档」有意义：选批量时必须藏起来。
        // SelectedItem 还没初始化时按"归档"处理，但只有文件夹模式才显示。
        string? modeTag = (FolderModeBox?.SelectedItem as ComboBoxItem)?.Tag as string;
        bool archive = modeTag != "Batch";
        bool folderMode = EncModeFolder?.IsChecked == true;

        ArchiveLevelBox.Visibility = folderMode && archive ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ArchiveLevel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ArchiveLevelBox?.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            CbxLog.Info($"归档等级：{tag}");
        }
    }

    /// <summary>按当前方案刷新说明文字——两种方案差别很大，必须写清楚。</summary>
    private void UpdateFolderHint()
    {
        if (FolderHintText is null)
        {
            return;
        }

        bool batch = (FolderModeBox?.SelectedItem as ComboBoxItem)?.Tag as string == "Batch";
        FolderHintText.Text = UiText.Get(batch ? "folder.hint.batch" : "folder.hint.archive");
    }

    /// <summary>
    /// 解归档：整包解到输出目录，冲突逐个询问（可「对之后所有冲突都使用这次选择」）。
    /// </summary>
    private async Task ExtractArchiveAsync(string archivePath, string outputDirectory, string password)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        ConflictChoice? globalChoice = null;

        var progress = new Progress<double>(p =>
        {
            DecProgress.Value = p * 100;
            DecProgressText.Text = $"{p * 100:F0}%";
        DecProgressText.Foreground = System.Windows.Media.Brushes.White;
        });

        Directory.CreateDirectory(outputDirectory);

        int count = await Task.Run(() => CbxArchive.ExtractAll(archivePath, outputDirectory, passwordBytes, path =>
        {
            return Dispatcher.Invoke(() =>
            {
                if (globalChoice.HasValue)
                {
                    if (globalChoice.Value == ConflictChoice.Cancel)
                    {
                        throw new CbxException("已按你的选择停止解压。");
                    }

                    return globalChoice.Value == ConflictChoice.Overwrite;
                }

                var dialog = new ConflictDialog(path) { Owner = this };
                dialog.ShowDialog();

                if (dialog.ApplyToAll)
                {
                    globalChoice = dialog.Choice;
                }

                if (dialog.Choice == ConflictChoice.Cancel)
                {
                    throw new CbxException("已按你的选择停止解压。");
                }

                return dialog.Choice == ConflictChoice.Overwrite;
            });
        }, progress));

        DecProgress.Value = 100;
        DecProgressText.Text = "100%";
        ScrubPassword(DecPassword);

        if (!SoundFeedback.PlaySuccess())
        {
            FlashStatusBar();
        }

        SetStatus(UiText.Get("archive.done", count, outputDirectory));
    }


    /// <summary>
    /// 过滤字符串必须是 "说明|模式" 形式；词条缺失时会变成 "!key!"，直接传会抛异常。
    /// 这里统一兜底，宁可退化成"所有文件"，也不能让点一下浏览就崩。
    /// </summary>
    private static string SafeFilter(string filter)
    {
        return filter.Contains('|') && !filter.StartsWith('!') ? filter : "所有文件 (*.*)|*.*";
    }
    /// <summary>
    /// 归档预览：口令 → 只解清单 → 勾选条目导出。
    /// 完全加密的归档（previewable=0）不提供这个入口。
    /// </summary>
    private async void PreviewArchive_Click(object sender, RoutedEventArgs e)
    {
        string input = DecInputBox.Text.Trim();
        string password = DecPassword.Password;

        if (input.Length == 0 || !File.Exists(input))
        {
            SetStatus(UiText.Get("error.pick.cbx"), error: true);
            return;
        }

        if (password.Length == 0)
        {
            SetStatus(UiText.Get("error.password.empty"), error: true);
            return;
        }

        CbxHeader header;

        try
        {
            header = Cbx.ReadHeader(input);
        }
        catch (CbxException ex)
        {
            SetStatus(ex.Message, error: true);
            return;
        }

        if (!header.IsArchive)
        {
            SetStatus(UiText.Get("preview.notarchive"), error: true);
            return;
        }

        if (!header.IsPreviewable)
        {
            SetStatus(UiText.Get("preview.notallowed"), error: true);
            return;
        }

        var preview = new ArchivePreviewWindow(input, password) { Owner = this };

        if (preview.ShowDialog() != true)
        {
            return;
        }

        string output = DecOutputBox.Text.Trim();

        if (output.Length == 0)
        {
            output = Path.GetDirectoryName(input) ?? ".";
        }

        string folderName = Path.GetFileNameWithoutExtension(input);
        string target = Path.Combine(output, folderName);

        if (Directory.Exists(target) || File.Exists(target))
        {
            target = MakeUniqueDirectory(target);
        }

        Directory.CreateDirectory(target);

        var passwordBytes = Encoding.UTF8.GetBytes(password);
        var chosen = preview.SelectedEntries;

        try
        {
            Begin(string.Empty);

            int count = await Task.Run(() => CbxArchive.ExtractAll(
                input, target, passwordBytes, null, null, CancellationToken.None, chosen));

            SetStatus(UiText.Get("archive.done", count, target));
        }
        catch (Exception ex)
        {
            CbxLog.Error("按勾选导出失败", ex);
            SetStatus(UiText.Get("folder.failed", ex.Message), error: true);
        }
        finally
        {
            End();
        }
    }

    /// <summary>
    /// 口令强度提示（粗粒度）。只在加密时显示——解密时不评判，
    /// 免得在旁人面前暗示"这个文件的口令很弱"。
    /// </summary>
    private void EncPassword_Changed(object sender, RoutedEventArgs e)
    {
        if (PwdStrengthText is null || EncPassword2 is null)
        {
            return;
        }

        string password = EncPassword2.Password;

        if (password.Length == 0)
        {
            PwdStrengthText.Text = string.Empty;
            return;
        }

        var level = PasswordStrength.Evaluate(password);
        string key = level switch
        {
            PasswordLevel.Strong => "strong",
            PasswordLevel.Medium => "medium",
            _ => "weak",
        };

        PwdStrengthText.Text = UiText.Get($"pwd.level.{key}") + "　·　" + UiText.Get($"pwd.hint.{key}");
        PwdStrengthText.Foreground = level switch
        {
            PasswordLevel.Strong => (System.Windows.Media.Brush)FindResource("BrushSuccess"),
            PasswordLevel.Medium => (System.Windows.Media.Brush)FindResource("BrushWarning"),
            _ => (System.Windows.Media.Brush)FindResource("BrushDanger"),
        };
    }

    /// <summary>提示音开关。</summary>
    private void SoundCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        SoundFeedback.Enabled = (sender as CheckBox)?.IsChecked == true;
        CbxLog.Info($"提示音：{(SoundFeedback.Enabled ? "开" : "关")}");
    }

    /// <summary>
    /// 出错时的统一弹窗：先响一声失败音，再弹带 OK 的对话框。
    /// 状态栏的红色文字仍然保留，作为关闭弹窗后的回看记录。
    /// </summary>
    private void ShowErrorDialog(string title, string message)
    {
        SoundFeedback.PlayFailure();

        // 用 None 而不是 Error：带图标的那种 MessageBox 会自己再播一次系统音，
        // 叠加起来就是"双响"，又吵又乱。
        AppDialog.Error(title, message, this);
    }

    private void SetStatus(string message, bool error = false)
    {
        // XAML 初始化期间控件还没构造完，ComboBox 的 IsSelected 会提前触发事件走到这里
        if (StatusText is null)
        {
            return;
        }

        StatusText.Text = message;
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource(error ? "BrushDanger" : "BrushText");
        _statusError = error;

        if (error)
        {
            StatusText.FontSize = 18;
            StatusText.FontWeight = FontWeights.Bold;
            CbxLog.Warn($"界面提示：{message}");
        }
    }
}
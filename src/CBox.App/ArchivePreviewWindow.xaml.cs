using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using CBox.Core;

namespace CBox.App;

/// <summary>
/// 归档预览：输入口令后**只解密清单**，把目录树显示出来，让用户勾选要导出的条目。
///
/// 这是"一般档"的核心体验：不用整包解开就能看到里面有什么。
/// 完全档不提供这个入口（头部 previewable=0）。
/// </summary>
public partial class ArchivePreviewWindow : Window
{
    private readonly List<(string Path, CheckBox Box)> _entries = new();

    public ArchivePreviewWindow(string archivePath, string password)
    {
        InitializeComponent();

        FilePath = archivePath;
        Password = password;

        IReadOnlyList<CbxArchive.Entry> manifest;

        try
        {
            manifest = CbxArchive.ReadManifest(archivePath, Encoding.UTF8.GetBytes(password));
        }
        catch (Exception ex)
        {
            AppDialog.Error(UiText.Get("preview.title"), UiText.Get("preview.failed", ex.Message), this);
            Close();
            return;
        }

        HeadingText.Text = UiText.Get("preview.heading", Path.GetFileName(archivePath), manifest.Count);
        BuildTree(manifest);
    }

    /// <summary>
    /// 按路径层级建树：每一级是一个可展开的文件夹节点，文件是带勾选的子节点。
    /// 之前平铺 + 按完整路径排序，会出现"根目录排在最下面"的迷惑显示。
    /// </summary>
    private void BuildTree(IReadOnlyList<CbxArchive.Entry> manifest)
    {
        var folders = new Dictionary<string, TreeViewItem>(StringComparer.OrdinalIgnoreCase);
        var rootNode = new TreeViewItem
        {
            Header = UiText.Get("preview.root"),
            IsExpanded = true,
            FontWeight = FontWeights.SemiBold,
        };

        EntryTree.Items.Add(rootNode);
        folders[string.Empty] = rootNode;

        foreach (var entry in manifest.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase))
        {
            var segments = entry.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (segments.Length == 0)
            {
                continue;
            }

            string currentPath = string.Empty;
            ItemsControl parent = rootNode;

            // 逐级建文件夹（或复用已建的）
            for (int i = 0; i < segments.Length - 1; i++)
            {
                string key = currentPath.Length == 0 ? segments[i] : $"{currentPath}/{segments[i]}";

                if (!folders.TryGetValue(key, out var folder))
                {
                    folder = new TreeViewItem
                    {
                        Header = segments[i],
                        IsExpanded = true,
                        FontWeight = FontWeights.SemiBold,
                    };

                    parent.Items.Add(folder);
                    folders[key] = folder;
                }

                parent = folder;
                currentPath = key;
            }

            var box = new CheckBox
            {
                Content = $"{segments[^1]}    {FormatSize(entry.Size)}",
                Margin = new Thickness(0, 1, 0, 1),
                IsChecked = true,
            };

            box.Checked += (_, _) => UpdateSelection();
            box.Unchecked += (_, _) => UpdateSelection();

            parent.Items.Add(box);
            _entries.Add((entry.Path, box));
        }

        UpdateSelection();
    }
    private static string FormatSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} {UiText.Get("common.bytes")}";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024.0:F1} KB";
        }

        return $"{bytes / 1024.0 / 1024.0:F1} MB";
    }

    private void UpdateSelection()
    {
        int selected = _entries.Count(e => e.Box.IsChecked == true);
        SelectionText.Text = UiText.Get("preview.selected", selected, _entries.Count);
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var entry in _entries)
        {
            entry.Box.IsChecked = true;
        }
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var entry in _entries)
        {
            entry.Box.IsChecked = false;
        }
    }

    private void Extract_Click(object sender, RoutedEventArgs e)
    {
        var chosen = _entries.Where(x => x.Box.IsChecked == true).Select(x => x.Path).ToList();

        if (chosen.Count == 0)
        {
            AppDialog.Info(UiText.Get("preview.title"), UiText.Get("preview.nothing"), this);
            return;
        }

        SelectedEntries = chosen;
        DialogResult = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    public string FilePath { get; }

    public string Password { get; }

    /// <summary>用户勾选要导出的条目；取消时为空。</summary>
    public IReadOnlyList<string> SelectedEntries { get; private set; } = Array.Empty<string>();
}

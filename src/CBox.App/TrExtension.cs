using CBox.Core;
using System.Windows.Markup;

namespace CBox.App;

/// <summary>
/// XAML 里取词条：<c>Text="{ui:Tr enc.start}"</c>
///
/// 注意：切换语言后需要重建窗口才能生效（这里不做实时刷新，
/// 因为语言切换在设置里，本来就会重开界面）。
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string key)
    {
        Key = key;
    }

    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        return UiText.Get(Key);
    }
}

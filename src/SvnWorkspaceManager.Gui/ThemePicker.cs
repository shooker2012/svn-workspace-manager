using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Styling;

namespace SvnWorkspaceManager.Gui;

public sealed class ThemePicker : ComboBox
{
    public ThemePicker()
    {
        Width = 150;
        ItemsSource = new[] { "石墨黑工作台", "浅色" };
        SelectedIndex = Application.Current?.RequestedThemeVariant == ThemeVariant.Light ? 1 : 0;
        SetValue(AutomationProperties.NameProperty, "界面主题");
        ToolTip.SetTip(this, "切换界面主题");
        SelectionChanged += (_, _) =>
        {
            if (Application.Current is { } app && SelectedIndex >= 0)
                ThemePalette.Select(app, SelectedIndex == 1);
        };
        ActualThemeVariantChanged += (_, _) =>
        {
            SelectedIndex = Application.Current?.RequestedThemeVariant == ThemeVariant.Light ? 1 : 0;
        };
    }

    protected override Type StyleKeyOverride => typeof(ComboBox);
}

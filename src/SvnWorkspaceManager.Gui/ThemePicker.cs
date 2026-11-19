using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;

namespace SvnWorkspaceManager.Gui;

public sealed class ThemePicker : ComboBox
{
    public ThemePicker()
    {
        Width = 150;
        ItemsSource = ThemePalette.Names;
        SelectedIndex = ThemePalette.CurrentIndex;
        SetValue(AutomationProperties.NameProperty, "界面主题");
        ToolTip.SetTip(this, "切换界面主题");
        SelectionChanged += (_, _) =>
        {
            if (Application.Current is { } app && SelectedIndex >= 0)
                ThemePalette.Select(app, SelectedIndex);
        };
    }

    protected override Type StyleKeyOverride => typeof(ComboBox);
}

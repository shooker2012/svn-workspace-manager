using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using SvnWorkspaceManager.Core.Configuration;

namespace SvnWorkspaceManager.Gui;

internal static class ThemePalette
{
    // Shared brushes update controls and existing task rows without reloading SVN data.
    private sealed record Swatch(string Key, string Dark, string Light)
    {
        public SolidColorBrush Brush { get; } = new(Color.Parse(Dark));
    }

    private static readonly Swatch[] Swatches =
    [
        new("Canvas", "#15181C", "#F4F6FA"),
        new("Panel", "#22262C", "#FFFFFF"),
        new("Inset", "#1C2025", "#F8FAFD"),
        new("Line", "#343A42", "#D9DEE7"),
        new("ControlLine", "#49515C", "#C9D2DF"),
        new("Text", "#E8EBEF", "#20242B"),
        new("Muted", "#B0B8C4", "#637083"),
        new("Hover", "#303842", "#EEF4FC"),
        new("Accent", "#9BC4FF", "#1557A6"),
        new("Selected", "#293D56", "#DCEBFF"),
        new("Primary", "#3166AD", "#2F6FEB"),
        new("PrimaryHover", "#3973BF", "#245BC7"),
        new("OnPrimary", "#FFFFFF", "#FFFFFF"),
        new("Success", "#88DBA8", "#237A3B"),
        new("SuccessFill", "#252E2B", "#DFF5E3"),
        new("SuccessLine", "#4F8B69", "#53A869"),
        new("Warning", "#F3C772", "#936400"),
        new("WarningFill", "#322D24", "#FFF4D6"),
        new("WarningLine", "#A88340", "#E0B84A"),
        new("Error", "#FFA6A3", "#B42318"),
        new("ErrorFill", "#38292D", "#FFE1E1"),
        new("ErrorHover", "#493135", "#FFE5E5"),
        new("ErrorLine", "#B66A6D", "#D64545"),
        new("Overlay", "#A60A0D10", "#990E1626"),
    ];

    private static readonly Dictionary<string, SolidColorBrush> BrushesByKey =
        Swatches.ToDictionary(swatch => swatch.Key, swatch => swatch.Brush);

    public static IBrush Muted => BrushesByKey[nameof(Muted)];
    public static IBrush Error => BrushesByKey[nameof(Error)];
    public static IBrush ErrorFill => BrushesByKey[nameof(ErrorFill)];
    public static IBrush Selected => BrushesByKey[nameof(Selected)];
    public static IBrush Accent => BrushesByKey[nameof(Accent)];
    public static IBrush Success => BrushesByKey[nameof(Success)];
    public static IBrush Warning => BrushesByKey[nameof(Warning)];
    public static IBrush SuccessFill => BrushesByKey[nameof(SuccessFill)];
    public static IBrush WarningFill => BrushesByKey[nameof(WarningFill)];
    public static IBrush Panel => BrushesByKey[nameof(Panel)];
    public static IBrush ErrorLine => BrushesByKey[nameof(ErrorLine)];
    public static IBrush Line => BrushesByKey[nameof(Line)];
    public static IBrush Text => BrushesByKey[nameof(Text)];
    public static IBrush SuccessLine => BrushesByKey[nameof(SuccessLine)];

    private static string PreferencePath => Path.Combine(ProjectLocator.DefaultApplicationDirectory, "gui-theme.txt");

    public static void Initialize(Application app)
    {
        foreach (var swatch in Swatches)
            app.Resources[swatch.Key] = swatch.Brush;
        var light = false;
        try { light = File.ReadAllText(PreferencePath).Trim() == "light"; }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        Apply(app, light);
    }

    public static void Select(Application app, bool light)
    {
        var requestedTheme = light ? ThemeVariant.Light : ThemeVariant.Dark;
        if (app.RequestedThemeVariant == requestedTheme)
            return;
        Apply(app, light);
        var path = PreferencePath;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporary, light ? "light" : "graphite");
            File.Move(temporary, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine($"无法保存界面主题：{ex.Message}");
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void Apply(Application app, bool light)
    {
        app.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        foreach (var swatch in Swatches)
            swatch.Brush.Color = Color.Parse(light ? swatch.Light : swatch.Dark);
    }
}

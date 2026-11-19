using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using SvnWorkspaceManager.Core.Configuration;

namespace SvnWorkspaceManager.Gui;

internal static class ThemePalette
{
    private sealed record Swatch(string Key, string Graphite, string Solarized, string Dracula, string Light)
    {
        public SolidColorBrush Brush { get; } = new(Color.Parse(Graphite));
    }

    private sealed record ThemeDefinition(string Id, string Name, ThemeVariant Variant);

    private static readonly ThemeDefinition[] Themes =
    [
        new("graphite", "石墨黑工作台", ThemeVariant.Dark),
        new("solarized-dark", "Solarized Dark", ThemeVariant.Dark),
        new("dracula", "Dracula", ThemeVariant.Dark),
        new("light", "浅色", ThemeVariant.Light),
    ];

    private static readonly Swatch[] Swatches =
    [
        new("Canvas", "#15181C", "#002B36", "#282A36", "#F4F6FA"),
        new("Panel", "#22262C", "#073642", "#343746", "#FFFFFF"),
        new("Inset", "#1C2025", "#002B36", "#21222C", "#F8FAFD"),
        new("Line", "#343A42", "#586E75", "#44475A", "#D9DEE7"),
        new("ControlLine", "#49515C", "#657B83", "#6272A4", "#C9D2DF"),
        new("Text", "#E8EBEF", "#EEE8D5", "#F8F8F2", "#20242B"),
        new("Muted", "#B0B8C4", "#93A1A1", "#BFBFBF", "#637083"),
        new("Hover", "#303842", "#0A4050", "#44475A", "#EEF4FC"),
        new("Accent", "#9BC4FF", "#268BD2", "#8BE9FD", "#1557A6"),
        new("Selected", "#293D56", "#164B5B", "#44475A", "#DCEBFF"),
        new("Primary", "#3166AD", "#268BD2", "#BD93F9", "#2F6FEB"),
        new("PrimaryHover", "#3973BF", "#2AA198", "#D6B5FF", "#245BC7"),
        new("OnPrimary", "#FFFFFF", "#FDF6E3", "#282A36", "#FFFFFF"),
        new("Success", "#88DBA8", "#859900", "#50FA7B", "#237A3B"),
        new("SuccessFill", "#252E2B", "#123D3A", "#243D32", "#DFF5E3"),
        new("SuccessLine", "#4F8B69", "#657B83", "#50FA7B", "#53A869"),
        new("Warning", "#F3C772", "#B58900", "#FFB86C", "#936400"),
        new("WarningFill", "#322D24", "#4A3A16", "#493A2A", "#FFF4D6"),
        new("WarningLine", "#A88340", "#B58900", "#FFB86C", "#E0B84A"),
        new("Error", "#FFA6A3", "#DC322F", "#FF5555", "#B42318"),
        new("ErrorFill", "#38292D", "#4A1E24", "#492B32", "#FFE1E1"),
        new("ErrorHover", "#493135", "#63252B", "#5C3540", "#FFE5E5"),
        new("ErrorLine", "#B66A6D", "#DC322F", "#FF5555", "#D64545"),
        new("Overlay", "#A60A0D10", "#A6002B36", "#A6282A36", "#990E1626"),
    ];

    private static readonly Dictionary<string, SolidColorBrush> BrushesByKey =
        Swatches.ToDictionary(swatch => swatch.Key, swatch => swatch.Brush);

    public static IReadOnlyList<string> Names => Themes.Select(theme => theme.Name).ToArray();
    public static int CurrentIndex { get; private set; }
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

        var themeId = "graphite";
        try
        {
            var saved = File.ReadAllText(PreferencePath).Trim();
            if (Themes.Any(theme => theme.Id == saved)) themeId = saved;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        Apply(app, FindTheme(themeId));
    }

    public static void Select(Application app, int index)
    {
        if (index < 0 || index >= Themes.Length) return;
        var theme = Themes[index];
        if (CurrentIndex == index) return;

        Apply(app, theme);
        var path = PreferencePath;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporary, theme.Id);
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

    private static ThemeDefinition FindTheme(string id) =>
        Themes.FirstOrDefault(theme => theme.Id == id) ?? Themes[0];

    private static void Apply(Application app, ThemeDefinition theme)
    {
        CurrentIndex = Array.IndexOf(Themes, theme);
        app.RequestedThemeVariant = theme.Variant;
        foreach (var swatch in Swatches)
        {
            var color = theme.Id switch
            {
                "solarized-dark" => swatch.Solarized,
                "dracula" => swatch.Dracula,
                "light" => swatch.Light,
                _ => swatch.Graphite,
            };
            swatch.Brush.Color = Color.Parse(color);
        }
    }
}

using MudBlazor;

namespace Akasha.Server.Shared;

/// <summary>Пресеты тем (светлая + тёмная палитра каждого): фиолетово-золотая, океан, лес.</summary>
public static class AkashaTheme
{
    public static MudTheme Get(string? name) => name switch
    {
        "ocean" => Ocean,
        "forest" => Forest,
        _ => Violet,
    };

    private static MudTheme Make(PaletteLight light, PaletteDark dark) => new()
    {
        PaletteLight = light,
        PaletteDark = dark,
        LayoutProperties = new LayoutProperties { DefaultBorderRadius = "10px" },
        Typography = new Typography
        {
            Default = new DefaultTypography
            {
                FontFamily = new[] { "Segoe UI", "Roboto", "system-ui", "sans-serif" },
            },
        },
    };

    /// <summary>Фиолетово-золотая (бренд, §7.1 ТЗ).</summary>
    public static readonly MudTheme Violet = Make(
        new PaletteLight
        {
            Primary = "#6C3FC5",
            Secondary = "#C9A227",
            Tertiary = "#8E6BD8",
            AppbarBackground = "#4A2A8F",
            DrawerBackground = "#F6F3FB",
            Background = "#FAF9FC",
            Surface = "#FFFFFF",
            Success = "#2E7D32",
            Error = "#B3261E",
        },
        new PaletteDark
        {
            Primary = "#B388FF",
            Secondary = "#FFD700",
            Tertiary = "#8E6BD8",
            AppbarBackground = "#241243",
            DrawerBackground = "#1A0B2E",
            Background = "#1A0B2E",
            Surface = "#241243",
            Success = "#66BB6A",
            Error = "#EF5350",
        });

    /// <summary>Океан — синий/бирюзовый.</summary>
    public static readonly MudTheme Ocean = Make(
        new PaletteLight
        {
            Primary = "#1565C0",
            Secondary = "#00838F",
            Tertiary = "#4FC3F7",
            AppbarBackground = "#0D47A1",
            DrawerBackground = "#E3F2FD",
            Background = "#F5FAFF",
            Surface = "#FFFFFF",
            Success = "#2E7D32",
            Error = "#B3261E",
        },
        new PaletteDark
        {
            Primary = "#4FC3F7",
            Secondary = "#80DEEA",
            Tertiary = "#81D4FA",
            AppbarBackground = "#0B2545",
            DrawerBackground = "#071A2F",
            Background = "#071A2F",
            Surface = "#0B2545",
            Success = "#66BB6A",
            Error = "#EF5350",
        });

    /// <summary>Лес — зелёный/янтарный.</summary>
    public static readonly MudTheme Forest = Make(
        new PaletteLight
        {
            Primary = "#2E7D32",
            Secondary = "#B26A00",
            Tertiary = "#66BB6A",
            AppbarBackground = "#1B5E20",
            DrawerBackground = "#E8F5E9",
            Background = "#F4FBF4",
            Surface = "#FFFFFF",
            Success = "#2E7D32",
            Error = "#B3261E",
        },
        new PaletteDark
        {
            Primary = "#81C784",
            Secondary = "#FFD54F",
            Tertiary = "#A5D6A7",
            AppbarBackground = "#14301A",
            DrawerBackground = "#0B1F10",
            Background = "#0B1F10",
            Surface = "#14301A",
            Success = "#66BB6A",
            Error = "#EF5350",
        });
}

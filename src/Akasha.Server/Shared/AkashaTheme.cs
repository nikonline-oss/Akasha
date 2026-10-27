using MudBlazor;

namespace Akasha.Server.Shared;

/// <summary>Фиолетово-золотая палитра бренда (§7.1 ТЗ).</summary>
public static class AkashaTheme
{
    public static MudTheme Current { get; } = new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#6C3FC5",        // фиолетовый
            Secondary = "#C9A227",      // золотой
            Tertiary = "#8E6BD8",
            AppbarBackground = "#4A2A8F",
            DrawerBackground = "#F6F3FB",
            Background = "#FAF9FC",
            Surface = "#FFFFFF",
            Success = "#2E7D32",
            Error = "#B3261E",
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#B388FF",        // светлый фиолетовый
            Secondary = "#FFD700",      // золотой
            Tertiary = "#8E6BD8",
            AppbarBackground = "#241243",
            DrawerBackground = "#1A0B2E",
            Background = "#1A0B2E",
            Surface = "#241243",
            Success = "#66BB6A",
            Error = "#EF5350",
        },
        LayoutProperties = new LayoutProperties { DefaultBorderRadius = "10px" },
        Typography = new Typography
        {
            Default = new DefaultTypography
            {
                FontFamily = new[] { "Segoe UI", "Roboto", "system-ui", "sans-serif" },
            },
        },
    };
}

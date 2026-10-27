namespace Akasha.Server;

/// <summary>Глобальное UI-состояние: язык и тема. Singleton, изменения рассылаются через <see cref="Changed"/>.</summary>
public sealed class AppState
{
    public string Language { get; private set; } = "ru";
    public string ThemeName { get; private set; } = "violet";
    public bool IsDarkMode { get; private set; }

    public event Action? Changed;

    public void SetLanguage(string? lang)
    {
        var next = lang == "en" ? "en" : "ru";
        if (next == Language) return;
        Language = next;
        Loc.Language = next;
        Changed?.Invoke();
    }

    public void SetTheme(string? name)
    {
        var next = name is "ocean" or "forest" or "violet" ? name : "violet";
        if (next == ThemeName) return;
        ThemeName = next;
        Changed?.Invoke();
    }

    public void SetDark(bool dark)
    {
        if (dark == IsDarkMode) return;
        IsDarkMode = dark;
        Changed?.Invoke();
    }

    public void ToggleLanguage() => SetLanguage(Language == "ru" ? "en" : "ru");
}

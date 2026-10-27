namespace Akasha.Server;

/// <summary>Локализация RU/EN по ключам. Текущий язык хранится в <see cref="AppState"/>.</summary>
public static class Loc
{
    public static string Language { get; set; } = "ru";

    public static string T(string key) =>
        (Language == "en" ? En : Ru).TryGetValue(key, out var v) ? v : key;

    public static string T(string key, params object[] args) => string.Format(T(key), args);

    private static readonly Dictionary<string, string> Ru = new()
    {
        // навигация
        ["nav.dashboard"] = "Дашборд",
        ["nav.projects"] = "Проекты",
        ["nav.search"] = "Поиск",
        ["nav.graph"] = "Граф",
        ["nav.approvals"] = "Апрувы",
        ["nav.scripts"] = "Скрипты",
        ["nav.agents"] = "Агенты",
        ["nav.secrets"] = "Секреты",
        ["nav.sync"] = "Синхронизация",
        ["nav.audit"] = "Аудит",
        ["nav.settings"] = "Настройки",
        ["nav.newEntry"] = "Новая запись",
        ["nav.logout"] = "Выйти",
        ["nav.menu"] = "Меню",
        ["nav.tagline"] = "Personal Context Vault",
        // настройки
        ["settings.title"] = "Настройки",
        ["settings.subtitle"] = "Язык, тема и оформление",
        ["settings.language"] = "Язык интерфейса",
        ["settings.languageHint"] = "Переключение применяется сразу и запоминается в браузере.",
        ["settings.appearance"] = "Оформление",
        ["settings.theme"] = "Цветовая тема",
        ["settings.dark"] = "Тёмная тема",
        ["settings.violet"] = "Фиолетово-золотая",
        ["settings.ocean"] = "Океан",
        ["settings.forest"] = "Лес",
        ["settings.preview"] = "Предпросмотр",
        ["settings.saved"] = "Сохранено",
        // общее
        ["common.save"] = "Сохранить",
        ["common.cancel"] = "Отмена",
        ["common.delete"] = "Удалить",
        ["common.create"] = "Создать",
        ["common.edit"] = "Изменить",
        ["common.run"] = "Запустить",
        ["common.close"] = "Закрыть",
        // страницы
        ["page.notfound"] = "Страница не найдена",
        ["page.entry"] = "Запись",
        ["page.projects"] = "Проекты",
        ["page.search"] = "Поиск",
        ["page.graph"] = "Граф связей",
        ["page.approvals"] = "Апрувы",
        ["page.scripts"] = "Скрипты (Structured JSON Queries)",
        ["page.agents"] = "Агенты",
        ["page.secrets"] = "Секреты",
        ["page.sync"] = "Синхронизация",
        ["page.audit"] = "Аудит",
        // дашборд
        ["dash.hero.title"] = "✦ Личное хранилище контекста",
        ["dash.hero.text"] = "Заметки, решения и источники о тебе и твоих проектах. AI-агенты читают, но меняют только через твоё одобрение.",
        ["dash.quick"] = "Быстрые действия",
        ["dash.newEntry"] = "+ Новая запись",
        ["dash.newProject"] = "+ Новый проект",
        ["dash.approvals"] = "Очередь апрувов ({0})",
        ["dash.search"] = "Поиск",
        ["dash.projects"] = "Проекты",
        ["dash.empty"] = "Пока пусто.",
        ["dash.recent"] = "Последние записи",
        // проекты
        ["projects.new"] = "Новый проект",
        ["projects.empty"] = "Пока нет проектов.",
        ["projects.name"] = "Название",
        ["projects.slug"] = "Slug",
        ["projects.desc"] = "Описание",
        ["projects.entries"] = "Записи проекта →",
        // поиск
        ["search.placeholder"] = "Поисковый запрос…",
        ["search.query"] = "Запрос",
        ["search.kind"] = "Тип",
        ["search.button"] = "Искать",
        ["search.empty"] = "Ничего не найдено.",
        // апрувы
        ["approvals.empty"] = "Очередь пуста.",
        ["approvals.pending"] = "Ожидают",
    };

    private static readonly Dictionary<string, string> En = new()
    {
        // navigation
        ["nav.dashboard"] = "Dashboard",
        ["nav.projects"] = "Projects",
        ["nav.search"] = "Search",
        ["nav.graph"] = "Graph",
        ["nav.approvals"] = "Approvals",
        ["nav.scripts"] = "Scripts",
        ["nav.agents"] = "Agents",
        ["nav.secrets"] = "Secrets",
        ["nav.sync"] = "Sync",
        ["nav.audit"] = "Audit",
        ["nav.settings"] = "Settings",
        ["nav.newEntry"] = "New entry",
        ["nav.logout"] = "Sign out",
        ["nav.menu"] = "Menu",
        ["nav.tagline"] = "Personal Context Vault",
        // settings
        ["settings.title"] = "Settings",
        ["settings.subtitle"] = "Language, theme and appearance",
        ["settings.language"] = "Interface language",
        ["settings.languageHint"] = "Applied immediately and remembered in the browser.",
        ["settings.appearance"] = "Appearance",
        ["settings.theme"] = "Color theme",
        ["settings.dark"] = "Dark mode",
        ["settings.violet"] = "Violet & gold",
        ["settings.ocean"] = "Ocean",
        ["settings.forest"] = "Forest",
        ["settings.preview"] = "Preview",
        ["settings.saved"] = "Saved",
        // common
        ["common.save"] = "Save",
        ["common.cancel"] = "Cancel",
        ["common.delete"] = "Delete",
        ["common.create"] = "Create",
        ["common.edit"] = "Edit",
        ["common.run"] = "Run",
        ["common.close"] = "Close",
        // pages
        ["page.notfound"] = "Page not found",
        ["page.entry"] = "Entry",
        ["page.projects"] = "Projects",
        ["page.search"] = "Search",
        ["page.graph"] = "Connection graph",
        ["page.approvals"] = "Approvals",
        ["page.scripts"] = "Scripts (Structured JSON Queries)",
        ["page.agents"] = "Agents",
        ["page.secrets"] = "Secrets",
        ["page.sync"] = "Sync",
        ["page.audit"] = "Audit",
        // dashboard
        ["dash.hero.title"] = "✦ Personal context vault",
        ["dash.hero.text"] = "Notes, decisions and sources about you and your projects. AI agents read, but change only with your approval.",
        ["dash.quick"] = "Quick actions",
        ["dash.newEntry"] = "+ New entry",
        ["dash.newProject"] = "+ New project",
        ["dash.approvals"] = "Approval queue ({0})",
        ["dash.search"] = "Search",
        ["dash.projects"] = "Projects",
        ["dash.empty"] = "Nothing yet.",
        ["dash.recent"] = "Recent entries",
        // projects
        ["projects.new"] = "New project",
        ["projects.empty"] = "No projects yet.",
        ["projects.name"] = "Name",
        ["projects.slug"] = "Slug",
        ["projects.desc"] = "Description",
        ["projects.entries"] = "Project entries →",
        // search
        ["search.placeholder"] = "Search query…",
        ["search.query"] = "Query",
        ["search.kind"] = "Kind",
        ["search.button"] = "Search",
        ["search.empty"] = "Nothing found.",
        // approvals
        ["approvals.empty"] = "Queue is empty.",
        ["approvals.pending"] = "Pending",
    };
}

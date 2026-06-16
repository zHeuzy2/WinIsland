using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinIsland;

public enum AppLanguage { Portuguese, English }

/// <summary>How the island expands: on hover (legacy) or on click.</summary>
public enum ExpandMode { Click, Hover }

/// <summary>Categories of system alert that can be silenced individually.</summary>
public enum AlertKind { Volume, Brightness, Battery, Connection }

/// <summary>A single checklist entry, persisted with the rest of the settings.</summary>
public sealed class TaskItem
{
    public string Text { get; set; } = "";
    public bool Done { get; set; }

    /// <summary>Optional delivery/due moment (local time). Null means "no date".</summary>
    public DateTime? DueAt { get; set; }

    /// <summary>
    /// Whether <see cref="DueAt"/> carries a meaningful time of day. When false the
    /// task is due "sometime that day" and only the date is shown.
    /// </summary>
    public bool HasTime { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>True when the task has a due moment already in the past and isn't done.</summary>
    [JsonIgnore]
    public bool IsOverdue =>
        !Done && DueAt is { } d && (HasTime ? d < DateTime.Now : d.Date < DateTime.Today);
}

/// <summary>Where the island anchors horizontally on the primary screen.</summary>
public enum IslandSide { Left, Center, Right }

/// <summary>
/// User-tweakable settings, persisted as JSON under %AppData%\WinIsland. Loading
/// and saving never throw: failures fall back to defaults so the app always runs.
/// </summary>
public sealed class AppSettings
{
    public IslandSide Position { get; set; } = IslandSide.Center;
    public AppLanguage Language { get; set; } = AppLanguage.Portuguese;
    public ExpandMode ExpandMode { get; set; } = ExpandMode.Click;
    public bool StartWithWindows { get; set; } = false;

    // Alert toggles — each can be silenced individually.
    public bool AlertVolume { get; set; } = true;
    public bool AlertBrightness { get; set; } = true;
    public bool AlertBattery { get; set; } = true;
    public bool AlertConnection { get; set; } = true;

    /// <summary>Theme accent (ARGB). Drives chips, music progress and accent text.</summary>
    public int AccentArgb { get; set; } = unchecked((int)0xFF0A84FF);

    /// <summary>
    /// Which toggleable tabs show in the nav, in display order. Settings is
    /// always shown (so the user can never lock themselves out) and is not
    /// listed here.
    /// </summary>
    public List<ViewKind> EnabledTabs { get; set; } = new()
    {
        ViewKind.Music, ViewKind.Timer, ViewKind.Camera, ViewKind.Tasks,
    };

    /// <summary>Checklist items shown in the Tasks tab.</summary>
    public List<TaskItem> Tasks { get; set; } = new();

    /// <summary>Palette offered in the settings color picker.</summary>
    [JsonIgnore]
    public static readonly int[] AccentPalette =
    {
        unchecked((int)0xFF0A84FF), // blue
        unchecked((int)0xFFBF5AF2), // purple
        unchecked((int)0xFFFF375F), // pink
        unchecked((int)0xFFFF9F0A), // orange
        unchecked((int)0xFF30D158), // green
        unchecked((int)0xFF64D2FF), // teal
    };

    /// <summary>Tabs that the user is allowed to toggle, in display order.</summary>
    [JsonIgnore]
    public static readonly ViewKind[] ToggleableTabs =
    {
        ViewKind.Music, ViewKind.Timer, ViewKind.Camera, ViewKind.Tasks,
    };

    [JsonIgnore]
    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinIsland");

    [JsonIgnore]
    private static string FilePath => Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Opts);
                if (s != null) return s;
            }
        }
        catch { /* fall back to defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Opts));
        }
        catch { /* non-fatal */ }
    }
}

/// <summary>
/// Tiny string localization. <see cref="Lang"/> is read on the render thread and
/// changed from the UI thread; T() returns the key itself for unknown entries.
/// </summary>
public static class Loc
{
    public static volatile AppLanguage Lang = AppLanguage.Portuguese;

    public static string T(string key)
    {
        var map = Lang == AppLanguage.English ? En : Pt;
        return map.TryGetValue(key, out var v) ? v : key;
    }

    private static readonly Dictionary<string, string> Pt = new()
    {
        ["tab.music"] = "Música",
        ["tab.timer"] = "Timer",
        ["tab.camera"] = "Câmera",
        ["tab.tasks"] = "Tarefas",
        ["tab.settings"] = "Ajustes",
        ["music.nothing"] = "Nada tocando agora",
        ["timer.start"] = "Iniciar",
        ["timer.pause"] = "Pausar",
        ["timer.reset"] = "Resetar",
        ["timer.done"] = "concluído",
        ["timer.timeup"] = "Tempo esgotado",
        ["preset.Foco"] = "Foco",
        ["preset.Pausa"] = "Pausa",
        ["preset.Longa"] = "Longa",
        ["camera.starting"] = "Iniciando câmera…",
        ["settings.position"] = "Posição na tela",
        ["settings.language"] = "Idioma",
        ["settings.accent"] = "Cor de destaque",
        ["settings.tabs"] = "Abas visíveis",
        ["settings.testnotif"] = "Testar notificação",
        ["settings.expandmode"] = "Expandir ao",
        ["settings.startup"] = "Iniciar com o Windows",
        ["settings.alerts"] = "Alertas ativos",
        ["expandmode.Click"] = "Clique",
        ["expandmode.Hover"] = "Hover",
        ["common.on"] = "Ligado",
        ["common.off"] = "Desligado",
        ["alert.volume"] = "Volume",
        ["alert.brightness"] = "Brilho",
        ["alert.battery"] = "Bateria",
        ["alert.connection"] = "Conexão",
        ["pin.toast.on"] = "Modo fixo",
        ["pin.toast.off"] = "Modo fixo desativado",
        ["hud.volume"] = "Volume",
        ["hud.brightness"] = "Brilho",
        ["hud.muted"] = "Mudo",
        ["battery.charging"] = "Carregando",
        ["battery.low"] = "Bateria fraca",
        ["battery.critical"] = "Bateria crítica",
        ["battery.full"] = "Carregamento completo",
        ["battery.unplugged"] = "Desconectado da tomada",
        ["wifi.connected"] = "Wi-Fi conectado",
        ["wifi.disconnected"] = "Wi-Fi desconectado",
        ["bt.connected"] = "Conectado",
        ["bt.disconnected"] = "Desconectado",
        ["test.title"] = "Notificação de teste",
        ["test.subtitle"] = "É assim que aparece na ilha",
        ["tasks.empty"] = "Toque em + para adicionar uma tarefa",
        ["tasks.new"] = "Nova tarefa",
        ["tasks.placeholder"] = "Escreva a tarefa…",
        ["tasks.date"] = "Data",
        ["tasks.time"] = "Hora",
        ["tasks.today"] = "Hoje",
        ["tasks.tomorrow"] = "Amanhã",
        ["tasks.nodate"] = "Sem data",
        ["tasks.addtime"] = "+ horário",
        ["tasks.save"] = "Salvar",
        ["tasks.cancel"] = "Cancelar",
        ["tasks.overdue"] = "Atrasada",
        ["date.today"] = "Hoje",
        ["date.tomorrow"] = "Amanhã",
        ["date.yesterday"] = "Ontem",
        ["pos.Left"] = "Esquerda",
        ["pos.Center"] = "Centro",
        ["pos.Right"] = "Direita",
        ["tray.exit"] = "Sair",
        ["tray.checkupdate"] = "Verificar atualizações",
        ["tray.installupdate"] = "Instalar atualização {0}",
        ["update.available.title"] = "Atualização disponível",
        ["update.available.sub"] = "Versão {0} — clique com o botão direito no ícone",
        ["update.checking"] = "Procurando atualizações…",
        ["update.uptodate"] = "Você já está na versão mais recente",
        ["update.downloading"] = "Baixando atualização…",
        ["update.failed"] = "Falha ao atualizar",
        ["update.ready"] = "Instalando atualização {0}…",
    };

    private static readonly Dictionary<string, string> En = new()
    {
        ["tab.music"] = "Music",
        ["tab.timer"] = "Timer",
        ["tab.camera"] = "Camera",
        ["tab.tasks"] = "Tasks",
        ["tab.settings"] = "Settings",
        ["music.nothing"] = "Nothing playing",
        ["timer.start"] = "Start",
        ["timer.pause"] = "Pause",
        ["timer.reset"] = "Reset",
        ["timer.done"] = "done",
        ["timer.timeup"] = "Time's up",
        ["preset.Foco"] = "Focus",
        ["preset.Pausa"] = "Break",
        ["preset.Longa"] = "Long",
        ["camera.starting"] = "Starting camera…",
        ["settings.position"] = "Screen position",
        ["settings.language"] = "Language",
        ["settings.accent"] = "Accent color",
        ["settings.tabs"] = "Visible tabs",
        ["settings.testnotif"] = "Test notification",
        ["settings.expandmode"] = "Expand on",
        ["settings.startup"] = "Start with Windows",
        ["settings.alerts"] = "Active alerts",
        ["expandmode.Click"] = "Click",
        ["expandmode.Hover"] = "Hover",
        ["common.on"] = "On",
        ["common.off"] = "Off",
        ["alert.volume"] = "Volume",
        ["alert.brightness"] = "Brightness",
        ["alert.battery"] = "Battery",
        ["alert.connection"] = "Connection",
        ["pin.toast.on"] = "Pinned",
        ["pin.toast.off"] = "Unpinned",
        ["hud.volume"] = "Volume",
        ["hud.brightness"] = "Brightness",
        ["hud.muted"] = "Muted",
        ["battery.charging"] = "Charging",
        ["battery.low"] = "Low battery",
        ["battery.critical"] = "Critical battery",
        ["battery.full"] = "Fully charged",
        ["battery.unplugged"] = "Unplugged",
        ["wifi.connected"] = "Wi-Fi connected",
        ["wifi.disconnected"] = "Wi-Fi disconnected",
        ["bt.connected"] = "Connected",
        ["bt.disconnected"] = "Disconnected",
        ["test.title"] = "Test notification",
        ["test.subtitle"] = "This is how it shows on the island",
        ["tasks.empty"] = "Tap + to add a task",
        ["tasks.new"] = "New task",
        ["tasks.placeholder"] = "Write the task…",
        ["tasks.date"] = "Date",
        ["tasks.time"] = "Time",
        ["tasks.today"] = "Today",
        ["tasks.tomorrow"] = "Tomorrow",
        ["tasks.nodate"] = "No date",
        ["tasks.addtime"] = "+ time",
        ["tasks.save"] = "Save",
        ["tasks.cancel"] = "Cancel",
        ["tasks.overdue"] = "Overdue",
        ["date.today"] = "Today",
        ["date.tomorrow"] = "Tomorrow",
        ["date.yesterday"] = "Yesterday",
        ["pos.Left"] = "Left",
        ["pos.Center"] = "Center",
        ["pos.Right"] = "Right",
        ["tray.exit"] = "Exit",
        ["tray.checkupdate"] = "Check for updates",
        ["tray.installupdate"] = "Install update {0}",
        ["update.available.title"] = "Update available",
        ["update.available.sub"] = "Version {0} — right-click the tray icon",
        ["update.checking"] = "Checking for updates…",
        ["update.uptodate"] = "You're on the latest version",
        ["update.downloading"] = "Downloading update…",
        ["update.failed"] = "Update failed",
        ["update.ready"] = "Installing update {0}…",
    };
}

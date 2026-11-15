using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenNetLimit.Core;
using OpenNetLimit.Core.Models;
using OpenNetLimit.UI.Services;

namespace OpenNetLimit.UI.ViewModels;

/// <summary>
/// The Rules tab: every rule and alert the service holds, plus the add, edit, toggle, delete,
/// import and export actions. It talks to the service only through <c>send</c>, so tests can
/// script the replies.
/// </summary>
public sealed class RulesViewModel : INotifyPropertyChanged
{
    /// <summary>The service drops request lines longer than this.</summary>
    internal const int MaxRequestLength = 65536;

    // The service's own options: enums go out as numbers and come back as names or numbers.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new EnumNameOrNumberConverter() }
    };

    private static readonly JsonSerializerOptions FileOptions = new() { WriteIndented = true };

    private readonly Func<string, Task<string?>> _send;
    private List<BandwidthRule> _rules = [];
    private List<BandwidthAlertRule> _alerts = [];
    private Dictionary<Guid, QuotaUsage> _quotas = [];
    private Func<Task>? _undo;
    private bool _loaded;
    private bool _alertsLoaded;

    public RulesViewModel(Func<string, Task<string?>> send)
    {
        _send = send;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<RuleRow> Rules { get; } = [];
    public ObservableCollection<AlertRow> Alerts { get; } = [];

    // Only a list the service sent can be empty. Before that the message line says what's wrong.
    public bool HasNoRules => _loaded && Rules.Count == 0 && ChangesRefused is null;
    public bool HasNoAlerts => _alertsLoaded && Alerts.Count == 0 && ChangesRefused is null;

    // A service turning changes away may not have loaded what's saved, so an empty list says why
    // instead of offering to add the first rule.
    public bool ShowsRulesRefused => _loaded && Rules.Count == 0 && ChangesRefused is not null;
    public bool ShowsAlertsRefused => _alertsLoaded && Alerts.Count == 0 && ChangesRefused is not null;

    private string? _changesRefused;
    /// <summary>Why the service turns changes away, or null when it takes them.</summary>
    public string? ChangesRefused
    {
        get => _changesRefused;
        set
        {
            if (SetField(ref _changesRefused, value))
                OnEmptyListsChanged();
        }
    }

    private void OnEmptyListsChanged()
    {
        OnPropertyChanged(nameof(HasNoRules));
        OnPropertyChanged(nameof(HasNoAlerts));
        OnPropertyChanged(nameof(ShowsRulesRefused));
        OnPropertyChanged(nameof(ShowsAlertsRefused));
    }

    private RuleRow? _selectedRule;
    public RuleRow? SelectedRule
    {
        get => _selectedRule;
        set
        {
            if (!SetField(ref _selectedRule, value)) return;
            OnPropertyChanged(nameof(CanChangeSelectedRule));
            OnPropertyChanged(nameof(ToggleLabel));
        }
    }

    private AlertRow? _selectedAlert;
    public AlertRow? SelectedAlert
    {
        get => _selectedAlert;
        set
        {
            if (SetField(ref _selectedAlert, value))
                OnPropertyChanged(nameof(CanChangeSelectedAlert));
        }
    }

    private bool _canEdit;
    /// <summary>The UI runs as administrator, which the service requires for every change.</summary>
    public bool CanEdit
    {
        get => _canEdit;
        set
        {
            if (!SetField(ref _canEdit, value)) return;
            OnPropertyChanged(nameof(CanChangeSelectedRule));
            OnPropertyChanged(nameof(CanChangeSelectedAlert));
            OnPropertyChanged(nameof(CanUndo));
        }
    }

    private string? _editHint;
    /// <summary>Why the edit buttons are off, or null when they're on so no empty tooltip shows.</summary>
    public string? EditHint
    {
        get => _editHint;
        set => SetField(ref _editHint, value);
    }

    public bool CanChangeSelectedRule => CanEdit && SelectedRule is not null;
    public bool CanChangeSelectedAlert => CanEdit && SelectedAlert is not null;
    public bool CanUndo => CanEdit && _undo is not null;

    public string ToggleLabel => LocalizationManager.Text(SelectedRule is { Rule.Enabled: false } ? "Rules_TurnOn" : "Rules_TurnOff");

    private string _message = string.Empty;
    public string Message
    {
        get => _message;
        private set => SetField(ref _message, value);
    }

    private bool _messageIsError;
    public bool MessageIsError
    {
        get => _messageIsError;
        private set => SetField(ref _messageIsError, value);
    }

    /// <summary>Reloads rules, quota use and alerts from the service.</summary>
    public async Task RefreshAsync()
    {
        if (await ReloadAsync() is { } error)
            ShowError(error);
    }

    /// <summary>
    /// Reloads everything. Returns what didn't load, or null. A list that didn't come keeps what
    /// it showed, so a failed read never looks like the service has no alerts.
    /// </summary>
    private async Task<string?> ReloadAsync()
    {
        var rulesReply = await _send("RULES");
        if (ReadList<BandwidthRule>(rulesReply) is not { } rules)
        {
            // The lists stay, but an empty one no longer claims there's nothing there.
            _loaded = _alertsLoaded = false;
            OnEmptyListsChanged();
            return ErrorOf(rulesReply) ?? LocalizationManager.Text("Rules_NoService");
        }
        var quotasReply = await _send("QUOTAS");
        var quotas = ReadList<QuotaUsage>(quotasReply);
        var alertsReply = await _send("ALERT_RULES");
        var alerts = ReadList<BandwidthAlertRule>(alertsReply);

        _rules = rules;
        if (quotas is not null)
            _quotas = quotas.GroupBy(q => q.RuleId).ToDictionary(g => g.Key, g => g.First());
        if (alerts is not null)
            _alerts = alerts;
        _alertsLoaded = alerts is not null;
        _loaded = true;
        Rebuild();
        if (quotas is null)
            return ErrorOf(quotasReply) ?? LocalizationManager.Text("Rules_NoService");
        if (alerts is null)
            return ErrorOf(alertsReply) ?? LocalizationManager.Text("Rules_NoService");
        return null;
    }

    /// <summary>
    /// Reloads after a change and says it's done. When the reload fails, both show: the change
    /// went through, and the list may be out of date.
    /// </summary>
    private async Task ReloadAfterChangeAsync(string done)
    {
        if (await ReloadAsync() is { } error)
            ShowError($"{done} {error}");
        else
            ShowInfo(done);
    }

    /// <summary>Sample rules and alerts for README screenshots.</summary>
    internal void ApplyMarketingFixture()
    {
        var weekdays = new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday };
        var onedrive = new BandwidthRule
        {
            Name = "Backups", ProcessName = "OneDrive", UploadBytesPerSecond = 512 * RuleDraft.BytesPerKilobyte,
            Quota = new QuotaConfig { LimitBytes = 2L * 1024 * RuleDraft.BytesPerMegabyte, Period = QuotaPeriod.Daily }
        };
        _rules =
        [
            new BandwidthRule
            {
                ProcessName = "chrome", GroupName = "Browsers",
                DownloadBytesPerSecond = 8 * RuleDraft.BytesPerMegabyte, UploadBytesPerSecond = RuleDraft.BytesPerMegabyte
            },
            new BandwidthRule
            {
                Name = "Work hours", ProcessName = "steam", GroupName = "Games", DownloadBytesPerSecond = 2 * RuleDraft.BytesPerMegabyte,
                Schedule = new RuleSchedule { StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(17, 0), ActiveDays = weekdays }
            },
            onedrive,
            new BandwidthRule { ProcessPath = @"C:\Program Files\Spotify\Spotify.exe", Action = RuleAction.Block, Enabled = false }
        ];
        _quotas = new() { [onedrive.Id] = new QuotaUsage(onedrive.Id, 1503L * RuleDraft.BytesPerMegabyte, false) };
        _alerts =
        [
            new BandwidthAlertRule { Name = "Big download", Direction = RuleDirection.Download, ThresholdBytesPerSecond = 50 * RuleDraft.BytesPerMegabyte },
            new BandwidthAlertRule { Name = "Stream upload", ProcessName = "obs64", Direction = RuleDirection.Upload, ThresholdBytesPerSecond = 4 * RuleDraft.BytesPerMegabyte, CooldownSeconds = 600 }
        ];
        _loaded = _alertsLoaded = true;
        CanEdit = true;
        Rebuild();
        SelectedRule = Rules.FirstOrDefault(r => r.Rule == onedrive);
    }

    /// <summary>Adds or replaces a rule. Returns what went wrong, or null when the service took it.</summary>
    public async Task<string?> SaveRuleAsync(BandwidthRule rule, bool isNew)
    {
        var json = JsonSerializer.Serialize(rule, JsonOptions);
        var error = await SendWriteAsync($"{(isNew ? "ADD_RULE" : "UPDATE_RULE")} {json}");
        if (error is not null)
            return error;
        _undo = null;
        await ReloadAfterChangeAsync(LocalizationManager.Format(isNew ? "Rules_Added" : "Rules_Saved", RuleRow.TargetOf(rule)));
        return null;
    }

    public async Task SetEnabledAsync(RuleRow row, bool enabled)
    {
        var rule = row.Rule.Clone();
        rule.Enabled = enabled;
        var error = await SendWriteAsync($"UPDATE_RULE {JsonSerializer.Serialize(rule, JsonOptions)}");
        if (error is not null)
        {
            ShowError(error);
            return;
        }
        _undo = null;
        await ReloadAfterChangeAsync(LocalizationManager.Format(enabled ? "Rules_TurnedOn" : "Rules_TurnedOff", row.Target));
    }

    /// <summary>Deletes right away. The message bar offers to put it back.</summary>
    public async Task DeleteRuleAsync(RuleRow row)
    {
        var error = await SendWriteAsync($"REMOVE_RULE {row.Rule.Id}");
        if (error is not null)
        {
            ShowError(error);
            return;
        }
        var removed = row.Rule.Clone();
        SetUndo(async () =>
        {
            var undoError = await SendWriteAsync($"ADD_RULE {JsonSerializer.Serialize(removed, JsonOptions)}");
            if (undoError is not null)
            {
                ShowError(undoError);
                return;
            }
            await ReloadAfterChangeAsync(LocalizationManager.Format("Rules_Restored", row.Target));
        });
        await ReloadAfterChangeAsync(LocalizationManager.Format("Rules_Deleted", row.Target));
    }

    public async Task<string?> SaveAlertAsync(BandwidthAlertRule alert, bool isNew)
    {
        var json = JsonSerializer.Serialize(alert, JsonOptions);
        var error = await SendWriteAsync($"{(isNew ? "ADD_ALERT_RULE" : "UPDATE_ALERT_RULE")} {json}");
        if (error is not null)
            return error;
        _undo = null;
        await ReloadAfterChangeAsync(LocalizationManager.Format(isNew ? "Alerts_Added" : "Alerts_Saved", AlertRow.NameOf(alert)));
        return null;
    }

    public async Task DeleteAlertAsync(AlertRow row)
    {
        var error = await SendWriteAsync($"REMOVE_ALERT_RULE {row.Alert.Id}");
        if (error is not null)
        {
            ShowError(error);
            return;
        }
        var removed = row.Alert;
        SetUndo(async () =>
        {
            var undoError = await SendWriteAsync($"ADD_ALERT_RULE {JsonSerializer.Serialize(removed, JsonOptions)}");
            if (undoError is not null)
            {
                ShowError(undoError);
                return;
            }
            await ReloadAfterChangeAsync(LocalizationManager.Format("Alerts_Restored", row.Name));
        });
        await ReloadAfterChangeAsync(LocalizationManager.Format("Alerts_Deleted", row.Name));
    }

    public async Task UndoAsync()
    {
        var undo = _undo;
        _undo = null;
        OnPropertyChanged(nameof(CanUndo));
        if (undo is not null)
            await undo();
    }

    /// <summary>Saves every rule to <paramref name="path"/> as an indented JSON array.</summary>
    public async Task ExportToAsync(string path)
    {
        var reply = await _send("EXPORT_RULES");
        if (ErrorOf(reply) is { } error)
        {
            ShowError(error);
            return;
        }
        if (ParseArray(reply) is not { } rules)
        {
            ShowError(LocalizationManager.Text("Rules_NoService"));
            return;
        }
        try
        {
            await File.WriteAllTextAsync(path, rules.ToJsonString(FileOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError(LocalizationManager.Format("Rules_FileError", ex.Message));
            return;
        }
        ShowInfo(LocalizationManager.Format("Rules_Exported", rules.Count, Path.GetFileName(path)));
    }

    /// <summary>Adds the rules in <paramref name="path"/> next to the current ones.</summary>
    public async Task ImportFromAsync(string path)
    {
        string text;
        try
        {
            text = await File.ReadAllTextAsync(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError(LocalizationManager.Format("Rules_FileError", ex.Message));
            return;
        }
        if (ParseImport(text) is not { } rules)
        {
            ShowError(LocalizationManager.Text("Rules_ImportNotList"));
            return;
        }
        var command = $"IMPORT_RULES {rules.ToJsonString()}";
        if (command.Length > MaxRequestLength)
        {
            ShowError(LocalizationManager.Text("Rules_ImportTooBig"));
            return;
        }
        var error = await SendWriteAsync(command);
        if (error is not null)
        {
            ShowError(error);
            return;
        }
        _undo = null;
        await ReloadAfterChangeAsync(LocalizationManager.Format("Rules_Imported", rules.Count, Path.GetFileName(path)));
    }

    /// <summary>Rebuilds the rows in the current language.</summary>
    public void Relocalize()
    {
        Rebuild();
        OnPropertyChanged(nameof(ToggleLabel));
    }

    private void Rebuild()
    {
        var selectedRuleId = SelectedRule?.Rule.Id;
        var selectedAlertId = SelectedAlert?.Alert.Id;

        Rules.Clear();
        foreach (var rule in _rules
                     .OrderBy(r => RuleRow.TargetOf(r), StringComparer.CurrentCultureIgnoreCase)
                     .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase))
            Rules.Add(new RuleRow(rule, _quotas.GetValueOrDefault(rule.Id)));

        Alerts.Clear();
        foreach (var alert in _alerts.OrderBy(AlertRow.NameOf, StringComparer.CurrentCultureIgnoreCase))
            Alerts.Add(new AlertRow(alert));

        SelectedRule = Rules.FirstOrDefault(r => r.Rule.Id == selectedRuleId);
        SelectedAlert = Alerts.FirstOrDefault(a => a.Alert.Id == selectedAlertId);
        OnEmptyListsChanged();
    }

    /// <summary>Sends a change. Returns what went wrong, or null when the service said ok.</summary>
    private async Task<string?> SendWriteAsync(string command)
    {
        var reply = await _send(command);
        if (ErrorOf(reply) is { } error)
            return error;
        return reply is not null && IsOk(reply) ? null : LocalizationManager.Text("Rules_NoService");
    }

    private void SetUndo(Func<Task> undo)
    {
        _undo = undo;
        OnPropertyChanged(nameof(CanUndo));
    }

    private void ShowInfo(string text)
    {
        MessageIsError = false;
        Message = text;
    }

    private void ShowError(string text)
    {
        MessageIsError = true;
        Message = text;
        OnPropertyChanged(nameof(CanUndo));
    }

    /// <summary>
    /// The error a reply carries: no reply means the service isn't reachable, and
    /// <c>{"error":"..."}</c> is the service turning the request down.
    /// </summary>
    internal static string? ErrorOf(string? reply)
    {
        if (reply is null)
            return LocalizationManager.Text("Rules_NoService");
        try
        {
            using var doc = JsonDocument.Parse(reply);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("error", out var error))
                return LocalizationManager.Format("Rules_ServiceError", error.GetString() ?? string.Empty);
        }
        catch (JsonException)
        {
            // Not JSON: the caller decides what that means.
        }
        return null;
    }

    private static bool IsOk(string reply)
    {
        try
        {
            using var doc = JsonDocument.Parse(reply);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("ok", out var ok) &&
                   ok.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static List<T>? ReadList<T>(string? reply)
    {
        if (reply is null)
            return null;
        try
        {
            return JsonSerializer.Deserialize<List<T>>(reply, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonArray? ParseArray(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        try
        {
            return JsonNode.Parse(text) as JsonArray;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The rules in an import file: an exported list, or the service's own rules file, which
    /// wraps the list as <c>{"version":1,"rules":[...]}</c>.
    /// </summary>
    private static JsonArray? ParseImport(string text)
    {
        var rules = ParseArray(text) ?? ParseWrapped(text);
        // Every rule is an object. A null or a number in the list isn't a rule file.
        return rules is not null && rules.All(rule => rule is JsonObject) ? rules : null;
    }

    private static JsonArray? ParseWrapped(string text)
    {
        try
        {
            return JsonNode.Parse(text) is JsonObject file && file["rules"] is JsonArray wrapped
                ? (JsonArray)wrapped.DeepClone()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>The part of a QUOTAS reply the list shows.</summary>
    internal sealed record QuotaUsage(Guid RuleId, long UsedBytes, bool IsExceeded);
}

/// <summary>One line of the rules list, worded in the language the row was built in.</summary>
public sealed class RuleRow
{
    internal RuleRow(BandwidthRule rule, RulesViewModel.QuotaUsage? quota)
    {
        Rule = rule;
        Target = TargetOf(rule);
        Name = rule.Name;
        ActionDisplay = LocalizationManager.Text(rule.Action switch
        {
            RuleAction.Block => "Rules_ActionBlock",
            RuleAction.Allow => "Rules_ActionAllow",
            _ => "Rules_ActionLimit"
        });
        LimitDisplay = rule.Action == RuleAction.Limit ? LimitOf(rule) : string.Empty;
        ScheduleDisplay = ScheduleOf(rule.Schedule);
        GroupDisplay = rule.GroupName ?? string.Empty;
        QuotaDisplay = QuotaOf(rule.Quota, quota);
        StateDisplay = LocalizationManager.Text(rule.Enabled ? "Rules_On" : "Rules_Off");
    }

    public BandwidthRule Rule { get; }
    public string Target { get; }
    public string Name { get; }
    public string ActionDisplay { get; }
    public string LimitDisplay { get; }
    public string ScheduleDisplay { get; }
    public string GroupDisplay { get; }
    public string QuotaDisplay { get; }
    public string StateDisplay { get; }

    internal static string TargetOf(BandwidthRule rule) => rule.ProcessPath ?? rule.ProcessName ?? string.Empty;

    private static string LimitOf(BandwidthRule rule)
    {
        var down = rule.Direction is RuleDirection.Upload ? 0 : rule.DownloadBytesPerSecond;
        var up = rule.Direction is RuleDirection.Download ? 0 : rule.UploadBytesPerSecond;
        var parts = new List<string>(2);
        if (down > 0) parts.Add("↓ " + ProcessViewModel.FormatBytes(down));
        if (up > 0) parts.Add("↑ " + ProcessViewModel.FormatBytes(up));
        return parts.Count == 0 ? LocalizationManager.Text("Limit_None") : string.Join("   ", parts);
    }

    internal static string ScheduleOf(RuleSchedule? schedule)
    {
        // The engine only uses a time window with both ends, so a lone time still means always.
        if (schedule is null ||
            (schedule is not { StartTime: not null, EndTime: not null } && schedule.ActiveDays is not { Length: > 0 }))
            return LocalizationManager.Text("Rules_Always");

        var culture = CultureInfo.CurrentCulture;
        var parts = new List<string>(2);
        if (schedule is { StartTime: { } start, EndTime: { } end })
            parts.Add(LocalizationManager.Format("Rules_TimeRange",
                start.ToString("t", culture), end.ToString("t", culture)));
        if (schedule.ActiveDays is { Length: > 0 } days)
            parts.Add(DaysOf(days, culture));
        return string.Join(", ", parts);
    }

    /// <summary>
    /// The days in week order, with three or more days in a row shortened to a range:
    /// "Mon to Fri, Sun" instead of six names.
    /// </summary>
    private static string DaysOf(DayOfWeek[] days, CultureInfo culture)
    {
        string Name(DayOfWeek day) => culture.DateTimeFormat.GetAbbreviatedDayName(day);
        var week = WeekOrder(culture).ToList();
        var parts = new List<string>();
        for (int i = 0; i < week.Count; i++)
        {
            if (!days.Contains(week[i]))
                continue;
            int last = i;
            while (last + 1 < week.Count && days.Contains(week[last + 1]))
                last++;
            if (last - i >= 2)
                parts.Add(LocalizationManager.Format("Rules_TimeRange", Name(week[i]), Name(week[last])));
            else
                parts.AddRange(week.Skip(i).Take(last - i + 1).Select(Name));
            i = last;
        }
        return string.Join(", ", parts);
    }

    /// <summary>The days of the week starting from the culture's first day.</summary>
    internal static IEnumerable<DayOfWeek> WeekOrder(CultureInfo culture)
    {
        var first = (int)culture.DateTimeFormat.FirstDayOfWeek;
        return Enumerable.Range(0, 7).Select(i => (DayOfWeek)((first + i) % 7));
    }

    private static string QuotaOf(QuotaConfig? quota, RulesViewModel.QuotaUsage? usage)
    {
        if (quota is null)
            return string.Empty;
        var text = LocalizationManager.Format(quota.Period switch
            {
                QuotaPeriod.Weekly => "Rules_QuotaWeekly",
                QuotaPeriod.Monthly => "Rules_QuotaMonthly",
                _ => "Rules_QuotaDaily"
            },
            ProcessViewModel.FormatTotalBytes(usage?.UsedBytes ?? 0),
            ProcessViewModel.FormatTotalBytes(quota.LimitBytes));
        return usage is { IsExceeded: true } ? $"{text} {LocalizationManager.Text("Rules_QuotaUsedUp")}" : text;
    }
}

/// <summary>One line of the alerts list.</summary>
public sealed class AlertRow
{
    internal AlertRow(BandwidthAlertRule alert)
    {
        Alert = alert;
        Name = NameOf(alert);
        Target = alert.ProcessPath ?? alert.ProcessName ?? LocalizationManager.Text("Alerts_EveryApp");
        DirectionDisplay = LocalizationManager.Text(alert.Direction switch
        {
            RuleDirection.Download => "Alerts_Download",
            RuleDirection.Upload => "Alerts_Upload",
            _ => "Alerts_Both"
        });
        ThresholdDisplay = ProcessViewModel.FormatBytes(alert.ThresholdBytesPerSecond);
        CooldownDisplay = LocalizationManager.Format("Alerts_CooldownMinutes",
            RuleDraft.Units(alert.CooldownSeconds, 60));
        StateDisplay = LocalizationManager.Text(alert.Enabled ? "Rules_On" : "Rules_Off");
    }

    public BandwidthAlertRule Alert { get; }
    public string Name { get; }
    public string Target { get; }
    public string DirectionDisplay { get; }
    public string ThresholdDisplay { get; }
    public string CooldownDisplay { get; }
    public string StateDisplay { get; }

    internal static string NameOf(BandwidthAlertRule alert) =>
        alert.Name.Length > 0 ? alert.Name : alert.ProcessName ?? alert.ProcessPath ?? LocalizationManager.Text("Alerts_EveryApp");
}

using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using OpenNetLimit.Core.Models;
using OpenNetLimit.UI.Services;
using OpenNetLimit.UI.ViewModels;
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfComboBoxItem = System.Windows.Controls.ComboBoxItem;
using WpfControl = System.Windows.Controls.Control;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace OpenNetLimit.UI;

/// <summary>
/// Adds or edits one rule. The save callback sends it to the service and returns the error to
/// show, so the dialog stays open with the user's fields when the service turns it down.
/// </summary>
public partial class RuleEditorDialog : Window
{
    private readonly BandwidthRule? _original;
    private readonly Func<BandwidthRule, Task<string?>> _save;
    private readonly WpfCheckBox[] _dayChecks = new WpfCheckBox[7];

    public RuleEditorDialog(BandwidthRule? original, Func<BandwidthRule, Task<string?>> save)
    {
        InitializeComponent();
        _original = original;
        _save = save;
        Title = HeaderTitle.Text = LocalizationManager.Text(original is null ? "RuleEditor_AddTitle" : "RuleEditor_EditTitle");

        EditorChoices.Fill(ActionCombo,
            (RuleAction.Limit, "Rules_ActionLimit"), (RuleAction.Block, "Rules_ActionBlock"), (RuleAction.Allow, "Rules_ActionAllow"));
        EditorChoices.Fill(PeriodCombo,
            (QuotaPeriod.Daily, "RuleEditor_PeriodDaily"), (QuotaPeriod.Weekly, "RuleEditor_PeriodWeekly"),
            (QuotaPeriod.Monthly, "RuleEditor_PeriodMonthly"));
        EditorChoices.Fill(QuotaActionCombo,
            (QuotaAction.Throttle, "RuleEditor_QuotaThrottle"), (QuotaAction.Block, "RuleEditor_QuotaBlock"),
            (QuotaAction.WarnOnly, "RuleEditor_QuotaWarn"));
        AddDayChecks();

        ShowDraft(original is null ? new RuleDraft() : RuleDraft.From(original));
        UpdateFieldStates();
        // The whole form fits a 1080p screen at 125%. A shorter one scrolls instead of cutting off the buttons.
        MaxHeight = SystemParameters.WorkArea.Height;
        Loaded += (_, _) => ProgramBox.Focus();
    }

    private void AddDayChecks()
    {
        var culture = CultureInfo.CurrentCulture;
        foreach (var day in RuleRow.WeekOrder(culture))
        {
            var check = new WpfCheckBox
            {
                Content = culture.DateTimeFormat.GetAbbreviatedDayName(day),
                Margin = new Thickness(0, 4, 12, 4)
            };
            AutomationProperties.SetName(check, culture.DateTimeFormat.GetDayName(day));
            _dayChecks[(int)day] = check;
            DaysPanel.Children.Add(check);
        }
    }

    private void ShowDraft(RuleDraft draft)
    {
        ProgramBox.Text = draft.ProcessName;
        PathBox.Text = draft.ProcessPath;
        NameBox.Text = draft.Name;
        EditorChoices.Select(ActionCombo, draft.Action);
        DownloadBox.Text = draft.DownloadKbps;
        UploadBox.Text = draft.UploadKbps;
        StartBox.Text = draft.ScheduleStart;
        EndBox.Text = draft.ScheduleEnd;
        for (var i = 0; i < 7; i++)
            _dayChecks[i].IsChecked = draft.Days[i];
        GroupBox.Text = draft.GroupName;
        EnabledCheck.IsChecked = draft.Enabled;
        QuotaCheck.IsChecked = draft.HasQuota;
        QuotaBox.Text = draft.QuotaMegabytes;
        EditorChoices.Select(PeriodCombo, draft.QuotaPeriod);
        EditorChoices.Select(QuotaActionCombo, draft.QuotaAction);
        ThrottleBox.Text = draft.ThrottleKbps;
    }

    private RuleDraft ReadDraft()
    {
        var draft = new RuleDraft
        {
            ProcessName = ProgramBox.Text,
            ProcessPath = PathBox.Text,
            Name = NameBox.Text,
            Action = EditorChoices.Selected(ActionCombo, RuleAction.Limit),
            DownloadKbps = DownloadBox.Text,
            UploadKbps = UploadBox.Text,
            ScheduleStart = StartBox.Text,
            ScheduleEnd = EndBox.Text,
            GroupName = GroupBox.Text,
            Enabled = EnabledCheck.IsChecked == true,
            HasQuota = QuotaCheck.IsChecked == true,
            QuotaMegabytes = QuotaBox.Text,
            QuotaPeriod = EditorChoices.Selected(PeriodCombo, QuotaPeriod.Daily),
            QuotaAction = EditorChoices.Selected(QuotaActionCombo, QuotaAction.Throttle),
            ThrottleKbps = ThrottleBox.Text
        };
        for (var i = 0; i < 7; i++)
            draft.Days[i] = _dayChecks[i].IsChecked == true;
        return draft;
    }

    private void OnChoiceChanged(object sender, RoutedEventArgs e) => UpdateFieldStates();

    /// <summary>Speeds only apply to Limit, and the data cap fields only when the cap is on.</summary>
    private void UpdateFieldStates()
    {
        // Selection and check events can fire while the window is still being built.
        if (ThrottlePanel is null || QuotaActionCombo is null)
            return;
        var limits = EditorChoices.Selected(ActionCombo, RuleAction.Limit) == RuleAction.Limit;
        DownloadBox.IsEnabled = UploadBox.IsEnabled = limits;
        var quota = QuotaCheck.IsChecked == true;
        QuotaBox.IsEnabled = PeriodCombo.IsEnabled = QuotaActionCombo.IsEnabled = ThrottleBox.IsEnabled = quota;
        ThrottlePanel.Visibility = EditorChoices.Selected(QuotaActionCombo, QuotaAction.Throttle) == QuotaAction.Throttle
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        if (EditorChoices.BrowseForProgram(this, PathBox.Text) is not { } path)
            return;
        PathBox.Text = path;
        if (ProgramBox.Text.Trim().Length == 0)
            ProgramBox.Text = System.IO.Path.GetFileName(path);
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        var result = ReadDraft().Build(_original);
        if (result.ErrorKey is { } key)
        {
            ShowError(LocalizationManager.Text(key), FieldFor(result.Field));
            return;
        }

        ErrorText.Visibility = Visibility.Collapsed;
        SaveButton.IsEnabled = false;
        var error = await _save(result.Rule!);
        SaveButton.IsEnabled = true;
        if (error is not null)
        {
            ShowError(error, null);
            return;
        }
        if (IsVisible)
            DialogResult = true;
    }

    private WpfControl? FieldFor(DraftField field) => field switch
    {
        DraftField.Target => ProgramBox,
        DraftField.Download => DownloadBox,
        DraftField.Upload => UploadBox,
        DraftField.Start => StartBox,
        DraftField.End => EndBox,
        DraftField.Quota => QuotaBox,
        DraftField.Throttle => ThrottleBox,
        _ => null
    };

    private void ShowError(string message, WpfControl? field)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        if (field is WpfTextBox box)
            box.SelectAll();
        field?.Focus();
        field?.BringIntoView();
    }
}

/// <summary>Helpers the rule and alert editors share.</summary>
internal static class EditorChoices
{
    /// <summary>
    /// Fills a combo box in code because the app's ComboBox template has no editable mode or
    /// item template, so each item carries its enum value in <c>Tag</c> and its label as content.
    /// </summary>
    public static void Fill<T>(WpfComboBox combo, params (T Value, string Key)[] choices) where T : struct, Enum
    {
        foreach (var (value, key) in choices)
            combo.Items.Add(new WpfComboBoxItem { Content = LocalizationManager.Text(key), Tag = value });
    }

    public static void Select<T>(WpfComboBox combo, T value) where T : struct, Enum
    {
        foreach (WpfComboBoxItem item in combo.Items)
        {
            if (item.Tag is T tag && EqualityComparer<T>.Default.Equals(tag, value))
            {
                combo.SelectedItem = item;
                return;
            }
        }
        combo.SelectedIndex = 0;
    }

    public static T Selected<T>(WpfComboBox combo, T fallback) where T : struct, Enum =>
        combo.SelectedItem is WpfComboBoxItem { Tag: T value } ? value : fallback;

    /// <summary>Asks for a program file, starting in the folder of the path already typed.</summary>
    public static string? BrowseForProgram(Window owner, string currentPath)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = LocalizationManager.Text("RuleEditor_BrowseFilter"),
            CheckFileExists = true
        };
        var folder = System.IO.Path.GetDirectoryName(currentPath.Trim().Trim('"'));
        if (!string.IsNullOrEmpty(folder) && System.IO.Directory.Exists(folder))
            dialog.InitialDirectory = folder;
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }
}

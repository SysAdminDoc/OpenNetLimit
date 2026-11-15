using System.Windows;
using OpenNetLimit.Core.Models;
using OpenNetLimit.UI.Services;
using OpenNetLimit.UI.ViewModels;
using WpfControl = System.Windows.Controls.Control;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace OpenNetLimit.UI;

/// <summary>Adds or edits one speed alert. Works like <see cref="RuleEditorDialog"/>.</summary>
public partial class AlertEditorDialog : Window
{
    private readonly BandwidthAlertRule? _original;
    private readonly Func<BandwidthAlertRule, Task<string?>> _save;

    public AlertEditorDialog(BandwidthAlertRule? original, Func<BandwidthAlertRule, Task<string?>> save)
    {
        InitializeComponent();
        _original = original;
        _save = save;
        Title = HeaderTitle.Text = LocalizationManager.Text(original is null ? "AlertEditor_AddTitle" : "AlertEditor_EditTitle");

        EditorChoices.Fill(DirectionCombo,
            (RuleDirection.Download, "Alerts_Download"), (RuleDirection.Upload, "Alerts_Upload"), (RuleDirection.Both, "Alerts_Both"));

        var draft = original is null ? new AlertDraft() : AlertDraft.From(original);
        ProgramBox.Text = draft.ProcessName;
        PathBox.Text = draft.ProcessPath;
        NameBox.Text = draft.Name;
        EditorChoices.Select(DirectionCombo, draft.Direction);
        ThresholdBox.Text = draft.ThresholdKbps;
        CooldownBox.Text = draft.CooldownMinutes;
        EnabledCheck.IsChecked = draft.Enabled;
        Loaded += (_, _) => ProgramBox.Focus();
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
        var result = new AlertDraft
        {
            ProcessName = ProgramBox.Text,
            ProcessPath = PathBox.Text,
            Name = NameBox.Text,
            Direction = EditorChoices.Selected(DirectionCombo, RuleDirection.Both),
            ThresholdKbps = ThresholdBox.Text,
            CooldownMinutes = CooldownBox.Text,
            Enabled = EnabledCheck.IsChecked == true
        }.Build(_original);
        if (result.ErrorKey is { } key)
        {
            ShowError(LocalizationManager.Text(key), key == "AlertEditor_NeedThreshold" ? ThresholdBox : CooldownBox);
            return;
        }

        ErrorText.Visibility = Visibility.Collapsed;
        SaveButton.IsEnabled = false;
        var error = await _save(result.Alert!);
        SaveButton.IsEnabled = true;
        if (error is not null)
        {
            ShowError(error, null);
            return;
        }
        if (IsVisible)
            DialogResult = true;
    }

    private void ShowError(string message, WpfControl? field)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        if (field is WpfTextBox box)
            box.SelectAll();
        field?.Focus();
    }
}

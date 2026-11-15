using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DataGrid = System.Windows.Controls.DataGrid;
using WpfScrollBar = System.Windows.Controls.Primitives.ScrollBar;

namespace OpenNetLimit.UI.Services;

/// <summary>
/// A WPF DataGrid doesn't select the row you right-click, so its context menu acted on
/// nothing (or on a stale row). Setting <c>SelectRowOnRightClick</c> selects it first.
/// </summary>
public static class DataGridRightClick
{
    public static readonly DependencyProperty SelectRowOnRightClickProperty =
        DependencyProperty.RegisterAttached(
            "SelectRowOnRightClick",
            typeof(bool),
            typeof(DataGridRightClick),
            new PropertyMetadata(false, OnChanged));

    public static bool GetSelectRowOnRightClick(DependencyObject element) =>
        (bool)element.GetValue(SelectRowOnRightClickProperty);

    public static void SetSelectRowOnRightClick(DependencyObject element, bool value) =>
        element.SetValue(SelectRowOnRightClickProperty, value);

    private static void OnChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not DataGrid grid)
            return;
        grid.PreviewMouseRightButtonDown -= OnPreviewMouseRightButtonDown;
        if (e.NewValue is true)
            grid.PreviewMouseRightButtonDown += OnPreviewMouseRightButtonDown;
    }

    private static void OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid || e.OriginalSource is not DependencyObject source)
            return;
        SelectRowAt(grid, source);
    }

    /// <summary>Selects the row that contains <paramref name="source"/>. Clicks off the rows clear the selection.</summary>
    public static void SelectRowAt(DataGrid grid, DependencyObject source)
    {
        for (var current = source; current is not null && current != grid; current = Parent(current))
        {
            switch (current)
            {
                case DataGridRow row:
                    grid.SelectedItem = row.Item;
                    row.Focus();
                    return;
                case DataGridColumnHeader:
                case WpfScrollBar:
                    return;
            }
        }

        grid.SelectedItem = null;
    }

    private static DependencyObject? Parent(DependencyObject element) =>
        element is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(element)
            : LogicalTreeHelper.GetParent(element);
}

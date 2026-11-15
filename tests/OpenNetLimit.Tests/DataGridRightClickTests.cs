using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using OpenNetLimit.UI.Services;
using OpenNetLimit.UI.ViewModels;
using Xunit;

namespace OpenNetLimit.Tests;

/// <summary>
/// Hosts a real DataGrid in a window that is never displayed and raises the same right-click
/// event WPF raises when the user right-clicks part of it.
/// </summary>
public class DataGridRightClickTests
{
    [Fact]
    public void RightClickingARow_SelectsIt() => WithGrid((grid, items) =>
    {
        RightClick(CellIn(grid, 1));

        Assert.Same(items[1], grid.SelectedItem);
    });

    [Fact]
    public void RightClickingAnotherRow_MovesTheSelection() => WithGrid((grid, items) =>
    {
        grid.SelectedItem = items[0];

        RightClick(CellIn(grid, 2));

        Assert.Same(items[2], grid.SelectedItem);
    });

    [Fact]
    public void RightClickingAColumnHeader_KeepsTheSelection() => WithGrid((grid, items) =>
    {
        grid.SelectedItem = items[0];

        RightClick(Descendant<DataGridColumnHeader>(grid, h => h.Column is not null));

        Assert.Same(items[0], grid.SelectedItem);
    });

    [Fact]
    public void RightClickingOffTheRows_ClearsTheSelection() => WithGrid((grid, items) =>
    {
        grid.SelectedItem = items[0];

        RightClick(Descendant<ScrollContentPresenter>(grid, _ => true));

        Assert.Null(grid.SelectedItem);
    });

    private const int WsChild = 0x40000000;
    private static readonly IntPtr MessageOnlyParent = new(-3);

    private static void WithGrid(Action<DataGrid, ProcessViewModel[]> test) => OnSta(() =>
    {
        var items = new[]
        {
            new ProcessViewModel { ProcessId = 10, ProcessName = "steam.exe" },
            new ProcessViewModel { ProcessId = 20, ProcessName = "chrome.exe" },
            new ProcessViewModel { ProcessId = 30, ProcessName = "svchost.exe" }
        };
        var grid = new DataGrid { ItemsSource = items, AutoGenerateColumns = false, CanUserAddRows = false };
        grid.Columns.Add(new DataGridTextColumn { Header = "Process", Binding = new Binding(nameof(ProcessViewModel.ProcessName)) });
        DataGridRightClick.SetSelectRowOnRightClick(grid, true);

        // A message-only window (parent HWND_MESSAGE) gives WPF a real presentation source to lay
        // out and route input in, and Windows never displays it.
        using var host = new HwndSource(new HwndSourceParameters("OpenNetLimit test host", 600, 400)
        {
            ParentWindow = MessageOnlyParent,
            WindowStyle = WsChild
        })
        {
            RootVisual = grid
        };
        grid.Measure(new System.Windows.Size(600, 400));
        grid.Arrange(new Rect(0, 0, 600, 400));
        grid.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

        test(grid, items);
    });

    private static void OnSta(Action test)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { test(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    private static DataGridCell CellIn(DataGrid grid, int rowIndex)
    {
        var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(rowIndex);
        Assert.NotNull(row);
        return Descendant<DataGridCell>(row, _ => true);
    }

    /// <summary>
    /// Raises PreviewMouseDown like the input system does. WPF turns it into the direct
    /// PreviewMouseRightButtonDown event on every element along the tunnel, the grid included.
    /// </summary>
    private static void RightClick(UIElement target)
    {
        target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
        {
            RoutedEvent = UIElement.PreviewMouseDownEvent,
            Source = target
        });
    }

    private static T Descendant<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
    {
        var pending = new Queue<DependencyObject>([root]);
        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            if (current is T found && match(found))
                return found;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(current); i++)
                pending.Enqueue(VisualTreeHelper.GetChild(current, i));
        }
        throw new InvalidOperationException($"No {typeof(T).Name} in the laid out grid.");
    }
}

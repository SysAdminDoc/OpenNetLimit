using System.Windows;
using OpenNetLimit.UI.ViewModels;

namespace OpenNetLimit.UI;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }
}

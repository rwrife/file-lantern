using System.Windows;
using FileLantern.App.ViewModels;

namespace FileLantern.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }
}

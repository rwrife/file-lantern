using System.Windows;
using FileLantern.App.ViewModels;
using FileLantern.Core.Indexing;

namespace FileLantern.App;

public partial class MainWindow : Window
{
    private readonly FileIndexDatabase _database;
    private readonly bool _seedIndexOnLoad;

    public MainWindow()
    {
        InitializeComponent();

        var appDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "file-lantern");
        var databasePath = Path.Combine(appDataDirectory, "index.db");

        _database = new FileIndexDatabase(databasePath);
        _seedIndexOnLoad = _database.CountFiles() == 0;

        DataContext = new MainViewModel(_database);

        Loaded += OnLoaded;
        Closed += (_, _) => _database.Dispose();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_seedIndexOnLoad)
        {
            return;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home) || !Directory.Exists(home))
        {
            return;
        }

        await Task.Run(() =>
        {
            var crawler = new FileCrawler(_database);
            crawler.Crawl(new[] { home });
        });
    }
}

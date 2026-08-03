using System.IO;
using System.Net.Http;
using System.Windows;
using FileLantern.App.Configuration;
using FileLantern.App.ViewModels;
using FileLantern.Core.Indexing;
using FileLantern.Core.LocalAi;

namespace FileLantern.App;

public partial class MainWindow : Window
{
    private readonly FileIndexDatabase _database;
    private readonly HttpClient _localAiHttpClient;
    private readonly bool _seedIndexOnLoad;
    private readonly string[] _indexedRoots;

    private LiveFileIndexUpdater? _liveIndexUpdater;

    public MainWindow()
    {
        InitializeComponent();

        var appDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "file-lantern");
        var databasePath = Path.Combine(appDataDirectory, "index.db");
        var settingsPath = Path.Combine(appDataDirectory, "settings.json");

        _database = new FileIndexDatabase(databasePath);
        _seedIndexOnLoad = _database.CountFiles() == 0;

        var appSettings = AppSettingsStore.Load(settingsPath);
        _localAiHttpClient = new HttpClient();
        var localAiTranslator = new LocalAiQueryTranslator(_localAiHttpClient, appSettings.LocalAi);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _indexedRoots = !string.IsNullOrWhiteSpace(home) && Directory.Exists(home)
            ? new[] { home }
            : Array.Empty<string>();

        DataContext = new MainViewModel(_database, localAiTranslator);

        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            _liveIndexUpdater?.Dispose();
            _localAiHttpClient.Dispose();
            _database.Dispose();
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_indexedRoots.Length == 0)
        {
            return;
        }

        if (_seedIndexOnLoad)
        {
            await Task.Run(() =>
            {
                var crawler = new FileCrawler(_database);
                crawler.Crawl(_indexedRoots);
            });
        }

        _liveIndexUpdater ??= new LiveFileIndexUpdater(_database, _indexedRoots);
    }
}

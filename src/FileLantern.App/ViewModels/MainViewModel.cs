using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using FileLantern.Core;
using FileLantern.Core.Indexing;

namespace FileLantern.App.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(150);

    private readonly FileIndexDatabase _database;
    private readonly Dispatcher? _dispatcher;
    private readonly RelayCommand _openSelectedFileCommand;
    private readonly RelayCommand _openSelectedFolderCommand;

    private CancellationTokenSource? _pendingSearch;
    private string _query = string.Empty;
    private SearchResultItem? _selectedResult;

    public MainViewModel(FileIndexDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _dispatcher = Application.Current?.Dispatcher;

        _openSelectedFileCommand = new RelayCommand(OpenSelectedFile, () => SelectedResult is not null);
        _openSelectedFolderCommand = new RelayCommand(OpenSelectedFolder, () => SelectedResult is not null);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Query
    {
        get => _query;
        set
        {
            if (_query == value)
            {
                return;
            }

            _query = value;
            OnPropertyChanged();
            ScheduleSearch();
        }
    }

    public ObservableCollection<SearchResultItem> Results { get; } = new();

    public SearchResultItem? SelectedResult
    {
        get => _selectedResult;
        set
        {
            if (ReferenceEquals(_selectedResult, value))
            {
                return;
            }

            _selectedResult = value;
            OnPropertyChanged();
            _openSelectedFileCommand.RaiseCanExecuteChanged();
            _openSelectedFolderCommand.RaiseCanExecuteChanged();
        }
    }

    public ICommand OpenSelectedFileCommand => _openSelectedFileCommand;

    public ICommand OpenSelectedFolderCommand => _openSelectedFolderCommand;

    private async void ScheduleSearch()
    {
        _pendingSearch?.Cancel();
        _pendingSearch?.Dispose();

        var querySnapshot = _query.Trim();
        if (string.IsNullOrWhiteSpace(querySnapshot))
        {
            ReplaceResults(Array.Empty<SearchResultItem>());
            return;
        }

        var cts = new CancellationTokenSource();
        _pendingSearch = cts;

        try
        {
            await Task.Delay(SearchDebounce, cts.Token);

            var results = await Task.Run(
                () => _database.Search(querySnapshot, limit: 500),
                cts.Token);

            if (cts.IsCancellationRequested)
            {
                return;
            }

            ReplaceResults(results);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer keystroke.
        }
    }

    private void ReplaceResults(IReadOnlyList<SearchResultItem> items)
    {
        RunOnUiThread(() =>
        {
            Results.Clear();
            foreach (var item in items)
            {
                Results.Add(item);
            }

            SelectedResult = Results.Count > 0 ? Results[0] : null;
        });
    }

    private void OpenSelectedFile()
    {
        if (SelectedResult is null)
        {
            return;
        }

        OpenPathWithShell(SelectedResult.FullPath);
    }

    private void OpenSelectedFolder()
    {
        if (SelectedResult is null)
        {
            return;
        }

        var fullPath = SelectedResult.FullPath;
        if (OperatingSystem.IsWindows())
        {
            var arguments = $"/select,\"{fullPath}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", arguments)
            {
                UseShellExecute = true
            });
            return;
        }

        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            OpenPathWithShell(directory);
        }
    }

    private static void OpenPathWithShell(string path)
    {
        Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true
        });
    }

    private void RunOnUiThread(Action action)
    {
        if (_dispatcher is null || _dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _dispatcher.Invoke(action);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

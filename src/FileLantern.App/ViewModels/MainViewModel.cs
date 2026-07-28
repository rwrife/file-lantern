using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using FileLantern.Core;

namespace FileLantern.App.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private string _query = string.Empty;

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
        }
    }

    public ObservableCollection<SearchResultItem> Results { get; } = new();

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

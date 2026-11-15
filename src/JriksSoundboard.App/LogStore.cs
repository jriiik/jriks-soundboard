using System.Collections.ObjectModel;
using System.Windows.Threading;

namespace JriksSoundboard.App;

/// <summary>Recent event lines for Settings → Diagnostics. Safe to add from any thread.</summary>
public sealed class LogStore(Dispatcher dispatcher)
{
    private const int Limit = 400;

    public ObservableCollection<string> Entries { get; } = [];

    public void Add(string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss}  {message}";
        if (dispatcher.CheckAccess())
        {
            Insert(line);
        }
        else
        {
            dispatcher.BeginInvoke(() => Insert(line));
        }
    }

    private void Insert(string line)
    {
        Entries.Insert(0, line);
        while (Entries.Count > Limit)
        {
            Entries.RemoveAt(Entries.Count - 1);
        }
    }
}

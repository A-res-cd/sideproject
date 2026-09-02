using System.Windows.Input;
using WindowsTranscriber.Core.Models;

namespace WindowsTranscriber.App.ViewModels;

public sealed class RecentApplicationShortcutViewModel
{
    public RecentApplicationShortcutViewModel(
        RecentApplication recentApplication,
        Func<RecentApplicationShortcutViewModel, Task> select)
    {
        RecentApplication = recentApplication;
        SelectCommand = new AsyncCommand(() => select(this));
    }

    public RecentApplication RecentApplication { get; }

    public string ProcessName => RecentApplication.ProcessName;

    public string DisplayName => RecentApplication.DisplayName;

    public ICommand SelectCommand { get; }
}

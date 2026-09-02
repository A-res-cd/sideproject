using System.Windows.Input;

namespace WindowsTranscriber.App.ViewModels;

public sealed class AsyncCommand : ICommand
{
    private readonly Func<Task> _execute;
    private bool _isExecuting;

    public AsyncCommand(Func<Task> execute)
    {
        _execute = execute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_isExecuting;

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
            return;

        try
        {
            _isExecuting = true;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            await _execute();
        }
        finally
        {
            _isExecuting = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
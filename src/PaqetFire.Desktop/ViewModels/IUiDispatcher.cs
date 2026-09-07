namespace PaqetFire.Desktop.ViewModels;

public interface IUiDispatcher
{
    bool HasThreadAccess { get; }
    bool TryEnqueue(Action action);
}

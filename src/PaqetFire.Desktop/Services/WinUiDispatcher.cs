using Microsoft.UI.Dispatching;
using PaqetFire.Desktop.ViewModels;

namespace PaqetFire.Desktop.Services;

public sealed class WinUiDispatcher(DispatcherQueue queue) : IUiDispatcher
{
    public bool HasThreadAccess => queue.HasThreadAccess;
    public bool TryEnqueue(Action action) => queue.TryEnqueue(() => action());
}

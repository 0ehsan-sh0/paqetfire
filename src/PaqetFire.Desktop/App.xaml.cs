using Microsoft.UI.Xaml;
using PaqetFire.Desktop.Presentation;

namespace PaqetFire.Desktop;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        // An exception escaping an async event handler must not take the control center down
        // with it; the broker keeps the route either way. Report it and keep running.
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var window = new MainWindow();
        _window = window;
        if (StartupCommand.IsBackgroundLaunch(Environment.GetCommandLineArgs().Skip(1)))
        {
            window.StartInBackground();
        }
        else
        {
            window.Activate();
        }

        // Program.Main listens for redirected launches before the UI exists; a request that
        // arrived during startup is delivered here.
        ActivationRedirect.Attach(window.ShowFromExternalActivation);
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        if (_window is null)
        {
            return;
        }

        args.Handled = true;
        _window.ReportUnexpectedError(args.Exception);
    }
}

using System.Windows;
using System.Windows.Threading;
using AudioEditor.App.Services;
using AudioEditor.App.ViewModels;

namespace AudioEditor.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        var window = new MainWindow();
        var viewModel = new MainViewModel(new DialogService(window), new RecentFiles(RecentFiles.DefaultStorePath));
        window.DataContext = viewModel;
        MainWindow = window;
        window.Show();

        // Files passed on the command line (or via "Open with") go straight onto the timeline;
        // a project file is opened.
        if (e.Args.Length > 0)
            _ = viewModel.OpenFilesAsync(e.Args);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "Something went wrong", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}

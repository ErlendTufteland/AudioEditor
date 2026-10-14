using System.IO;
using System.Windows;
using AudioEditor.App.Services;
using AudioEditor.App.ViewModels;
using Microsoft.Win32;

namespace AudioEditor.App.Views;

public partial class ExportWindow : Window
{
    private readonly ExportViewModel viewModel;

    public ExportWindow(ExportDialogInput input)
    {
        InitializeComponent();
        DataContext = viewModel = new ExportViewModel(input);
    }

    public ExportRequest? Result { get; private set; }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        var input = viewModel.Input;
        var dialog = new SaveFileDialog
        {
            Title = "Export as",
            Filter = viewModel.FileFilter,
            DefaultExt = viewModel.Extension,
            AddExtension = true,
            OverwritePrompt = true,
            FileName = input.FileName + viewModel.Extension,
        };
        if (input.Folder is { } folder && Directory.Exists(folder))
            dialog.InitialDirectory = folder;

        if (dialog.ShowDialog(this) != true)
            return;

        // The encoder picks the container from the extension, so make sure it matches.
        string path = dialog.FileName;
        if (!viewModel.AllowedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            path += viewModel.Extension;

        Result = new ExportRequest(viewModel.BuildOptions(), path);
        DialogResult = true;
    }
}

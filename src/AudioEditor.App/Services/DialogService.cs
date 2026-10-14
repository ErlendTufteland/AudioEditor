using System.IO;
using System.Windows;
using AudioEditor.App.Views;
using AudioEditor.Core;
using Microsoft.Win32;

namespace AudioEditor.App.Services;

public sealed record ExportDialogInput(
    OutputFormat Suggested,
    ExportOptions? Previous,
    TimeSpan Length,
    int ClipCount,
    string? Folder,
    string FileName);

public sealed record ExportRequest(ExportOptions Options, string Path);

/// <summary>What to do with unsaved changes before a project is closed.</summary>
public enum UnsavedChangesChoice
{
    Save,
    Discard,
    Cancel,
}

public interface IDialogService
{
    IReadOnlyList<string> PickAudioFiles();

    string? PickProjectToOpen(string? folder);

    /// <summary>Asks where to save a project. The path that comes back always has the project extension.</summary>
    string? PickProjectSavePath(string name, string? folder);

    UnsavedChangesChoice AskToSaveChanges(string projectName);

    /// <summary>Tells which of a project's audio files are missing; true if the user wants to look for them.</summary>
    bool AskToFindMissingFiles(IReadOnlyList<string> paths);

    /// <summary>Lets the user find a file that has moved; null if they give up.</summary>
    string? FindMissingFile(string path, string? folder);

    ExportRequest? AskForExport(ExportDialogInput input);

    void ShowError(string title, string message);
}

public sealed class DialogService(Window owner) : IDialogService
{
    public const string AudioFileFilter =
        "Audio files|*.wav;*.mp3;*.flac;*.m4a;*.aac;*.wma;*.aif;*.aiff;*.mp4|All files|*.*";

    public const string ProjectFileFilter = $"Audio Editor projects|*{ProjectFile.Extension}";

    private const int MaxListedFiles = 8;

    public IReadOnlyList<string> PickAudioFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Add audio files",
            Filter = AudioFileFilter,
            Multiselect = true,
        };
        return dialog.ShowDialog(owner) == true ? dialog.FileNames : [];
    }

    public string? PickProjectToOpen(string? folder)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open project",
            Filter = ProjectFileFilter + "|All files|*.*",
        };
        if (folder is not null && Directory.Exists(folder))
            dialog.InitialDirectory = folder;
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }

    public string? PickProjectSavePath(string name, string? folder)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save project as",
            Filter = ProjectFileFilter,
            DefaultExt = ProjectFile.Extension,
            AddExtension = true,
            OverwritePrompt = true,
            FileName = name + ProjectFile.Extension,
        };
        if (folder is not null && Directory.Exists(folder))
            dialog.InitialDirectory = folder;
        if (dialog.ShowDialog(owner) != true)
            return null;

        // "Song v1.2" counts as having an extension, so the dialog doesn't add one.
        string path = dialog.FileName;
        return ProjectFile.IsProjectFile(path) ? path : path + ProjectFile.Extension;
    }

    public UnsavedChangesChoice AskToSaveChanges(string projectName) =>
        MessageBox.Show(owner, $"Do you want to save the changes to {projectName}?", "Audio Editor",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Warning) switch
        {
            MessageBoxResult.Yes => UnsavedChangesChoice.Save,
            MessageBoxResult.No => UnsavedChangesChoice.Discard,
            _ => UnsavedChangesChoice.Cancel,
        };

    public bool AskToFindMissingFiles(IReadOnlyList<string> paths)
    {
        var lines = paths.Take(MaxListedFiles).ToList();
        if (paths.Count > MaxListedFiles)
            lines.Add($"…and {paths.Count - MaxListedFiles} more");

        string message = paths.Count == 1
            ? "This audio file in the project could not be found:"
            : $"These {paths.Count} audio files in the project could not be found:";
        message += Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, lines)
            + Environment.NewLine + Environment.NewLine
            + $"Do you want to look for {(paths.Count == 1 ? "it" : "them")}? Clips whose file is not found are left out.";
        return MessageBox.Show(owner, message, "Missing audio files", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    public string? FindMissingFile(string path, string? folder)
    {
        string name = Path.GetFileName(path);
        var dialog = new OpenFileDialog
        {
            Title = $"Find {name}",
            Filter = $"{name}|{name}|{AudioFileFilter}",
            FileName = name,
        };
        if (folder is not null && Directory.Exists(folder))
            dialog.InitialDirectory = folder;
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }

    public ExportRequest? AskForExport(ExportDialogInput input)
    {
        var window = new ExportWindow(input) { Owner = owner };
        return window.ShowDialog() == true ? window.Result : null;
    }

    public void ShowError(string title, string message) =>
        MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
}

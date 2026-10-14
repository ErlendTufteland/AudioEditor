using System.IO;
using System.Text.Json;

namespace AudioEditor.App.Services;

/// <summary>
/// The projects opened or saved most recently, newest first, kept in a small JSON file. The file
/// is read again before every change, so windows open at the same time don't undo each other's
/// changes. It is only a convenience: if it can't be read or written, the list is just empty.
/// </summary>
public sealed class RecentFiles(string storePath)
{
    private const int MaxCount = 10;

    public static string DefaultStorePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AudioEditor", "recent-projects.json");

    public IReadOnlyList<string> Paths { get; private set; } = Read(storePath);

    public void Add(string path) => Update(paths =>
    {
        paths.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        paths.Insert(0, path);
    });

    public void Remove(string path) =>
        Update(paths => paths.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)));

    private void Update(Action<List<string>> change)
    {
        var paths = Read(storePath);
        change(paths);
        Paths = paths.Take(MaxCount).ToList();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);
            File.WriteAllText(storePath, JsonSerializer.Serialize(Paths));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static List<string> Read(string path)
    {
        try
        {
            var paths = JsonSerializer.Deserialize<List<string?>>(File.ReadAllText(path)) ?? [];
            return paths.OfType<string>().Where(p => Path.IsPathFullyQualified(p)).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }
}

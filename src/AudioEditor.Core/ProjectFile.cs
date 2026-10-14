using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioEditor.Core;

/// <summary>
/// Reads and writes project files. A project is JSON that refers to its audio files both by full
/// path and by path relative to the project file, so a folder holding a project and its audio
/// can be moved or copied as a whole.
/// </summary>
public static class ProjectFile
{
    public const string Extension = ".aeproj";

    /// <summary>Written into every project, so other JSON files are not taken for one.</summary>
    private const string FormatName = "AudioEditor.Project";

    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static bool IsProjectFile(string path) =>
        string.Equals(Path.GetExtension(path), Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads a project. Each clip gets the path of its file as found now (see
    /// <see cref="FindClipFile"/>); a clip whose file can't be found keeps the full path it was saved with.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// The file is not a project, is damaged, or was saved by a newer version.
    /// </exception>
    public static Project Load(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string folder = Path.GetDirectoryName(fullPath)!;

        ProjectDocument? document;
        try
        {
            using var stream = File.OpenRead(fullPath);
            document = JsonSerializer.Deserialize<ProjectDocument>(stream, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The file is not an Audio Editor project, or it is damaged.", ex);
        }

        if (document is null || document.Format != FormatName)
            throw new InvalidDataException("The file is not an Audio Editor project.");
        if (document.Version > CurrentVersion)
            throw new InvalidDataException("The project was saved by a newer version of Audio Editor. Update Audio Editor to open it.");

        var clips = new List<ProjectClip>();
        foreach (var clip in document.Clips ?? [])
        {
            if (clip is null || FindClipFile(clip.Path, clip.RelativePath, folder) is not { } file)
                throw new InvalidDataException("The project is damaged: a clip has no file.");
            clips.Add(new ProjectClip(file)
            {
                TrimStart = Finite(clip.TrimStart),
                TrimEnd = clip.TrimEnd is double end && double.IsFinite(end) ? end : null,
                GainDb = Finite(clip.GainDb),
                FadeIn = Finite(clip.FadeIn),
                FadeOut = Finite(clip.FadeOut),
            });
        }

        var grid = document.BeatGrid ?? new BeatGridDocument();
        var export = document.Export;
        return new Project
        {
            Clips = clips,
            TempoMap = new TempoMap(
                (grid.Tempos ?? []).OfType<TempoDocument>().Select(t => new TempoChange(t.Bar, t.Bpm)),
                grid.BeatsPerBar,
                grid.Offset),
            IsGridEnabled = grid.Enabled,
            SnapTo = Enum.IsDefined(grid.SnapTo) ? grid.SnapTo : GridResolution.Beat,
            IsMetronomeOn = grid.Metronome,
            MetronomeVolume = double.IsFinite(grid.MetronomeVolume) ? Math.Clamp(grid.MetronomeVolume, 0, 1) : 0.5,
            Export = ToExportOptions(export),
            ExportFolder = export?.Folder is { Length: > 0 } exportFolder && Path.IsPathFullyQualified(exportFolder) ? exportFolder : null,
        };
    }

    /// <summary>
    /// Writes a project. The file is written under a temporary name first and then moved into
    /// place, so if saving fails, an earlier version of the file is left as it was.
    /// </summary>
    public static void Save(Project project, string path)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string fullPath = Path.GetFullPath(path);
        string folder = Path.GetDirectoryName(fullPath)!;
        var document = new ProjectDocument
        {
            Format = FormatName,
            Version = CurrentVersion,
            Clips = project.Clips.Select(clip =>
            {
                string file = Path.GetFullPath(clip.FilePath);
                string relative = Path.GetRelativePath(folder, file);
                return new ClipDocument
                {
                    Path = file,
                    // A file on another drive has no relative path.
                    RelativePath = Path.IsPathRooted(relative) ? null : relative,
                    TrimStart = clip.TrimStart,
                    TrimEnd = clip.TrimEnd,
                    GainDb = clip.GainDb,
                    FadeIn = clip.FadeIn,
                    FadeOut = clip.FadeOut,
                };
            }).ToList<ClipDocument?>(),
            BeatGrid = new BeatGridDocument
            {
                Enabled = project.IsGridEnabled,
                SnapTo = project.SnapTo,
                BeatsPerBar = project.TempoMap.BeatsPerBar,
                Offset = project.TempoMap.Offset,
                Tempos = project.TempoMap.Changes.Select(c => new TempoDocument { Bar = c.Bar, Bpm = c.Bpm }).ToList<TempoDocument?>(),
                Metronome = project.IsMetronomeOn,
                MetronomeVolume = project.MetronomeVolume,
            },
            Export = project.Export is null && project.ExportFolder is null ? null : new ExportDocument
            {
                Format = project.Export?.Format,
                SampleRate = project.Export?.Output.SampleRate,
                Channels = project.Export?.Output.Channels,
                BitRate = project.Export?.BitRate,
                Folder = project.ExportFolder,
            },
        };

        Directory.CreateDirectory(folder);
        string temporary = Path.Combine(folder, $"{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                JsonSerializer.Serialize(stream, document, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, fullPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>
    /// Finds a clip's file. A file that was saved inside the project's folder is looked for there
    /// first, so a copy of the folder uses its own copies of the audio. Any other file is looked
    /// for at its full path first, so a project file that was copied on its own still finds the
    /// originals. Returns the full path when the file is found nowhere, or null if there is no path at all.
    /// </summary>
    /// <param name="fullPath">The full path the file was saved with.</param>
    /// <param name="relativePath">Its path relative to the project's folder, if it has one.</param>
    /// <param name="projectFolder">The folder the project file is in now.</param>
    public static string? FindClipFile(string? fullPath, string? relativePath, string projectFolder)
    {
        string? saved = fullPath is { Length: > 0 } && Path.IsPathFullyQualified(fullPath) ? Path.GetFullPath(fullPath) : null;
        string? nearby = relativePath is { Length: > 0 } && !Path.IsPathRooted(relativePath)
            ? Path.GetFullPath(Path.Combine(projectFolder, relativePath))
            : null;

        string folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectFolder)) + Path.DirectorySeparatorChar;
        bool inside = nearby is not null && nearby.StartsWith(folder, StringComparison.OrdinalIgnoreCase);
        string?[] candidates = inside ? [nearby, saved] : [saved, nearby];
        return candidates.FirstOrDefault(p => p is not null && File.Exists(p)) ?? saved ?? nearby;
    }

    private static ExportOptions? ToExportOptions(ExportDocument? export)
    {
        // Settings the export dialog can't show are dropped rather than failing the whole project.
        if (export is not { Format: { } format, SampleRate: > 0 and int sampleRate, Channels: (1 or 2) and int channels } || !Enum.IsDefined(format))
            return null;
        return new ExportOptions(format, new OutputFormat(sampleRate, channels), export.BitRate is > 0 and int bitRate ? bitRate : 192_000);
    }

    private static double Finite(double value) => double.IsFinite(value) ? value : 0;

    // The file format. Property names are written in camelCase.

    private sealed class ProjectDocument
    {
        public string? Format { get; init; }
        public int Version { get; init; }
        public List<ClipDocument?>? Clips { get; init; }
        public BeatGridDocument? BeatGrid { get; init; }
        public ExportDocument? Export { get; init; }
    }

    private sealed class ClipDocument
    {
        public string? Path { get; init; }
        public string? RelativePath { get; init; }
        public double TrimStart { get; init; }
        public double? TrimEnd { get; init; }
        public double GainDb { get; init; }
        public double FadeIn { get; init; }
        public double FadeOut { get; init; }
    }

    private sealed class BeatGridDocument
    {
        public bool Enabled { get; init; }
        public GridResolution SnapTo { get; init; } = GridResolution.Beat;
        public int BeatsPerBar { get; init; } = 4;
        public double Offset { get; init; }
        public List<TempoDocument?>? Tempos { get; init; }
        public bool Metronome { get; init; }
        public double MetronomeVolume { get; init; } = 0.5;
    }

    private sealed class TempoDocument
    {
        public int Bar { get; init; }
        public double Bpm { get; init; }
    }

    private sealed class ExportDocument
    {
        public ExportFormat? Format { get; init; }
        public int? SampleRate { get; init; }
        public int? Channels { get; init; }
        public int? BitRate { get; init; }
        public string? Folder { get; init; }
    }
}

namespace AudioEditor.Core.Tests;

public class ProjectFileTests
{
    [Fact]
    public void Saved_project_opens_unchanged()
    {
        using var folder = new TempFolder();
        var project = new Project
        {
            Clips =
            [
                new ProjectClip(Touch(folder.File("a.wav"))) { TrimStart = 0.1, TrimEnd = 12.345678901234, GainDb = -3.5, FadeIn = 0.25, FadeOut = 2 },
                new ProjectClip(Touch(folder.File(@"Audio\b.wav"))) { TrimStart = 1.0 / 3 },
            ],
            TempoMap = new TempoMap([new TempoChange(1, 96), new TempoChange(17, 100.5)], beatsPerBar: 3, offset: 0.123),
            IsGridEnabled = true,
            SnapTo = GridResolution.HalfBeat,
            IsMetronomeOn = true,
            MetronomeVolume = 0.8,
            Export = new ExportOptions(ExportFormat.Mp3, new OutputFormat(48000, 1), 256_000),
            ExportFolder = folder.Path,
        };
        string path = folder.File("song.aeproj");

        ProjectFile.Save(project, path);
        var loaded = ProjectFile.Load(path);

        Assert.Equal(project.Clips, loaded.Clips);
        Assert.Equal(project.TempoMap.Changes, loaded.TempoMap.Changes);
        Assert.Equal(3, loaded.TempoMap.BeatsPerBar);
        Assert.Equal(0.123, loaded.TempoMap.Offset);
        Assert.Equal(project with { Clips = loaded.Clips, TempoMap = loaded.TempoMap }, loaded);
    }

    [Fact]
    public void Reads_a_version_1_file()
    {
        using var folder = new TempFolder();
        string path = folder.File("song.aeproj");
        File.WriteAllText(path, """
            {
              "format": "AudioEditor.Project",
              "version": 1,
              "clips": [
                { "path": "C:\\Music\\a.wav", "trimStart": 0.5, "trimEnd": 30.25, "gainDb": -3, "fadeIn": 0, "fadeOut": 2 },
                { "path": "C:\\Music\\b.wav" }
              ],
              "beatGrid": {
                "enabled": true,
                "snapTo": "quarterBeat",
                "beatsPerBar": 3,
                "offset": 0.12,
                "tempos": [ { "bar": 1, "bpm": 96 }, { "bar": 17, "bpm": 100 } ],
                "metronome": true,
                "metronomeVolume": 0.25
              },
              "export": { "format": "flac", "sampleRate": 48000, "channels": 1, "folder": "C:\\Exports" }
            }
            """);

        var project = ProjectFile.Load(path);

        Assert.Equal(
            [
                new ProjectClip(@"C:\Music\a.wav") { TrimStart = 0.5, TrimEnd = 30.25, GainDb = -3, FadeOut = 2 },
                new ProjectClip(@"C:\Music\b.wav"),
            ],
            project.Clips);
        Assert.Equal([new TempoChange(1, 96), new TempoChange(17, 100)], project.TempoMap.Changes);
        Assert.Equal(3, project.TempoMap.BeatsPerBar);
        Assert.Equal(0.12, project.TempoMap.Offset);
        Assert.True(project.IsGridEnabled);
        Assert.Equal(GridResolution.QuarterBeat, project.SnapTo);
        Assert.True(project.IsMetronomeOn);
        Assert.Equal(0.25, project.MetronomeVolume);
        Assert.Equal(new ExportOptions(ExportFormat.Flac, new OutputFormat(48000, 1)), project.Export);
        Assert.Equal(@"C:\Exports", project.ExportFolder);
    }

    [Fact]
    public void Odd_values_are_cleaned_up()
    {
        using var folder = new TempFolder();
        string path = folder.File("song.aeproj");
        File.WriteAllText(path, """
            {
              "format": "AudioEditor.Project",
              "version": 1,
              "beatGrid": { "snapTo": 99, "beatsPerBar": 0, "tempos": [ null, { "bar": 1, "bpm": 5000 } ], "metronomeVolume": 7 },
              "export": { "format": "wav16", "sampleRate": 44100, "channels": 6, "folder": "relative\\folder" }
            }
            """);

        var project = ProjectFile.Load(path);

        Assert.Empty(project.Clips);
        Assert.Equal(GridResolution.Beat, project.SnapTo);
        Assert.Equal(1, project.TempoMap.BeatsPerBar);
        Assert.Equal([new TempoChange(1, TempoMap.MaxBpm)], project.TempoMap.Changes);
        Assert.Equal(1, project.MetronomeVolume);
        Assert.Null(project.Export);
        Assert.Null(project.ExportFolder);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{ "clips": [] }""")]
    [InlineData("""{ "format": "AudioEditor.Project", "version": 2 }""")]
    [InlineData("""{ "format": "AudioEditor.Project", "version": 1, "clips": [ {} ] }""")]
    [InlineData("""{ "format": "AudioEditor.Project", "version": 1, "clips": [ null ] }""")]
    [InlineData("""{ "format": "AudioEditor.Project", "version": 1, "clips": [ { "path": "C:\\a.wav", "trimStart": "soon" } ] }""")]
    public void Rejects_files_that_are_not_projects_it_can_read(string json)
    {
        using var folder = new TempFolder();
        string path = folder.File("song.aeproj");
        File.WriteAllText(path, json);

        Assert.Throws<InvalidDataException>(() => ProjectFile.Load(path));
    }

    [Fact]
    public void Moved_folder_finds_its_audio_next_to_the_project()
    {
        using var folder = new TempFolder();
        string a = Touch(folder.File(@"Song\Audio\a.wav"));
        ProjectFile.Save(new Project { Clips = [new ProjectClip(a)] }, folder.File(@"Song\song.aeproj"));
        Directory.Move(folder.File("Song"), folder.File("Moved"));

        var loaded = ProjectFile.Load(folder.File(@"Moved\song.aeproj"));

        Assert.Equal(folder.File(@"Moved\Audio\a.wav"), Assert.Single(loaded.Clips).FilePath);
    }

    [Fact]
    public void Copied_folder_uses_its_own_copy_of_the_audio()
    {
        using var folder = new TempFolder();
        string a = Touch(folder.File(@"Song\a.wav"));
        ProjectFile.Save(new Project { Clips = [new ProjectClip(a)] }, folder.File(@"Song\song.aeproj"));
        string copy = Touch(folder.File(@"Copy\a.wav"));
        File.Copy(folder.File(@"Song\song.aeproj"), folder.File(@"Copy\song.aeproj"));

        Assert.Equal(copy, Assert.Single(ProjectFile.Load(folder.File(@"Copy\song.aeproj")).Clips).FilePath);
        Assert.Equal(a, Assert.Single(ProjectFile.Load(folder.File(@"Song\song.aeproj")).Clips).FilePath);
    }

    [Fact]
    public void Audio_outside_the_project_folder_is_looked_for_at_its_full_path_first()
    {
        using var folder = new TempFolder();
        string a = Touch(folder.File(@"Music\a.wav"));
        ProjectFile.Save(new Project { Clips = [new ProjectClip(a)] }, folder.File(@"Projects\song.aeproj"));

        // Copied on its own, the project's relative path (..\Music\a.wav) leads to another file.
        string other = Touch(folder.File(@"Elsewhere\Music\a.wav"));
        Directory.CreateDirectory(folder.File(@"Elsewhere\Projects"));
        File.Copy(folder.File(@"Projects\song.aeproj"), folder.File(@"Elsewhere\Projects\song.aeproj"));
        Assert.Equal(a, Assert.Single(ProjectFile.Load(folder.File(@"Elsewhere\Projects\song.aeproj")).Clips).FilePath);

        // Once the original has gone, the relative path is the best guess left.
        File.Delete(a);
        Assert.Equal(other, Assert.Single(ProjectFile.Load(folder.File(@"Elsewhere\Projects\song.aeproj")).Clips).FilePath);
    }

    [Fact]
    public void Missing_file_keeps_the_path_it_was_saved_with()
    {
        using var folder = new TempFolder();
        string a = Touch(folder.File(@"Song\a.wav"));
        string path = folder.File(@"Song\song.aeproj");
        ProjectFile.Save(new Project { Clips = [new ProjectClip(a)] }, path);
        File.Delete(a);

        Assert.Equal(a, Assert.Single(ProjectFile.Load(path).Clips).FilePath);
    }

    [Fact]
    public void Saving_replaces_the_file_and_leaves_nothing_else_behind()
    {
        using var folder = new TempFolder();
        string path = folder.File("song.aeproj");
        File.WriteAllText(path, "old");

        ProjectFile.Save(new Project { IsGridEnabled = true }, path);

        Assert.True(ProjectFile.Load(path).IsGridEnabled);
        Assert.Equal([path], Directory.GetFiles(folder.Path));
    }

    [Fact]
    public void Failed_save_leaves_the_old_file_as_it_was()
    {
        using var folder = new TempFolder();
        string path = folder.File("song.aeproj");
        ProjectFile.Save(new Project { IsGridEnabled = true }, path);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => ProjectFile.Save(new Project(), path));

            Assert.True(ProjectFile.Load(path).IsGridEnabled);
            Assert.Equal([path], Directory.GetFiles(folder.Path));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Theory]
    [InlineData(@"C:\Music\Song.AEPROJ", true)]
    [InlineData(@"C:\Music\Song.aeproj.wav", false)]
    [InlineData("Song", false)]
    public void Recognises_project_files_by_extension(string path, bool expected) =>
        Assert.Equal(expected, ProjectFile.IsProjectFile(path));

    /// <summary>Creates a file (and its folder). Its content doesn't matter: projects never read the audio.</summary>
    private static string Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "audio");
        return path;
    }
}

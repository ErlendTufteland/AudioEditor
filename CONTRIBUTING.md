# Contributing

Contributions are welcome. Every change goes through a pull request: `main` is protected, so
nobody pushes to it directly.

1. Fork the repository and create a branch from `main`.
2. Make your change. The build has no warnings and should stay that way, and the tests must pass:

   ```bash
   dotnet build AudioEditor.slnx
   dotnet test
   ```

   Only `AudioEditor.Core` has tests. For changes to the app, say in the pull request how you
   checked them in the running app.
3. Open a pull request against `main` that says what changed and why.

[CLAUDE.md](CLAUDE.md) describes how the code fits together (the timeline model, the audio
pipeline, the beat grid, project files and the UI conventions); it's worth a read before a
larger change.

By opening a pull request you agree that your contribution is released under this project's
[MIT License](LICENSE).

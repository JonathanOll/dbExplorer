# dbExplorer: working rules

- After each development, once everything is tested (`dotnet test`) and finished, commit and push directly to `main`. No
  branch or pull request is needed. Do not commit work that is half done or whose tests fail.
- Follow the UI rules of [`docs/ui/README.md`](docs/ui/README.md) (theme tokens, no colour in a view, keyboard focus visible).
- Screenshots of the screens are regenerated with `dotnet run --project tools/UiShots -- docs/ui/after <scene>`.

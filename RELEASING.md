# Releasing

Releases are created by pushing a **version tag**. Pushing to `main` does not release anything.

1. Bump `<Version>` in `Directory.Build.props` (for example `1.1.0`). Every project picks it up.
2. Add a `## 1.1.0` section to `CHANGELOG.md`. Its text becomes the GitHub release notes (if the section is missing, the notes link to the changelog).
3. Commit both changes and merge them to `main`.
4. Tag and push the tag:

   ```powershell
   git tag v1.1.0
   git push origin main v1.1.0
   ```

The **Release** workflow (`.github/workflows/release.yml`) runs on the tag, and:

- fails immediately if the tag does not equal `v` + the `Directory.Build.props` `<Version>` (so a tag can never ship a binary stamped with a different version);
- publishes the self-contained single-file wizard (`SmartMailboxCopyApp.exe`) and command line (`SmartMailboxCopy.exe`) for `win-x64`;
- creates the GitHub release with a zip of both and the wizard exe attached;
- never replaces an existing release (to republish, delete the release manually first, or bump the version).

CI (`.github/workflows/ci.yml`) builds every push and pull request to `main`.

To check a release build locally first:

```powershell
dotnet publish src/SmartMailboxCopy.App/SmartMailboxCopy.App.csproj -c Release --self-contained true -r win-x64 -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
dotnet publish src/SmartMailboxCopy.Cli/SmartMailboxCopy.Cli.csproj -c Release --self-contained true -r win-x64 -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

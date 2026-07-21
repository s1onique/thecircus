# .NET environment preflight

For any task touching F#, .NET project files, Fantomas, or a Make target
that invokes dotnet, run this cheap preflight before claiming that the SDK
is unavailable:

```bash
command -v dotnet >/dev/null 2>&1 &&
    dotnet --version >/dev/null 2>&1
```

When that fails, probe the known user installation directly:

```bash
if [ -x "$HOME/.dotnet/dotnet" ]; then
    "$HOME/.dotnet/dotnet" --version
fi
```

Classification:

- **PATH probe passes**: continue with the requested build or test.
- **PATH probe fails but the absolute host works**: report "The .NET SDK is
  installed, but this Cline environment is not configured to find it." Do not
  report that .NET is unavailable.
- **Both probes fail**: report the exact probes and outputs. Do not install or
  upgrade the SDK unless the active ACT explicitly authorizes installation.

Environment repair authority: **ACT-CIRCUS-DOTNET-USER-PATH-PERSISTENCE01**.

## Notes

- This preflight is cheap: no network, no restore, no recursive filesystem scan.
- The environment.d file (`~/.config/environment.d/20-circus-dotnet.conf`) provides
  persistent PATH for systemd user sessions.
- Shell profiles source `~/.config/circus/dotnet-env.sh` for login and interactive shells.
- A fresh terminal or editor restart may be required after environment changes.

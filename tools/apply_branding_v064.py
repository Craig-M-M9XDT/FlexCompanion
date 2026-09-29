from pathlib import Path

TEXT_EXTS = {'.md', '.cs', '.xaml', '.axaml', '.csproj', '.yml', '.yaml', '.sh', '.ps1', '.desktop', '.bat'}

for p in Path('.').rglob('*'):
    if not p.is_file() or '.git' in p.parts or p.suffix.lower() not in TEXT_EXTS:
        continue
    if p.name.startswith('CHANGES-v0.'):
        continue
    try:
        s = p.read_text(encoding='utf-8')
    except UnicodeDecodeError:
        continue
    n = s.replace('FLEX COMPANION', 'FLEX CONTROL COMPANION').replace('Flex Companion', 'Flex Control Companion')
    if n != s:
        p.write_text(n, encoding='utf-8', newline='\n')

for rel in ('FlexCompanion.csproj', 'Pi/FlexCompanion.Pi.csproj'):
    p = Path(rel)
    s = p.read_text(encoding='utf-8')
    s = s.replace('<Version>0.6.3</Version>', '<Version>0.6.4</Version>')
    p.write_text(s, encoding='utf-8', newline='\n')

p = Path('Pi/README-PI.md')
s = p.read_text(encoding='utf-8')
s = s.replace('Flex Control Companion v0.6.2', 'Flex Control Companion v0.6.4')
p.write_text(s, encoding='utf-8', newline='\n')

Path('.github/workflows/publish-v0.6.3.yml').write_text("""name: v0.6.3 archived

on:
  workflow_dispatch:

jobs:
  archived:
    runs-on: ubuntu-latest
    steps:
      - run: echo \"v0.6.3 is archived. Use the existing GitHub release assets; current development is Flex Control Companion v0.6.4.\"
""", encoding='utf-8', newline='\n')

Path('.github/workflows/publish-v0.6.4.yml').write_text("""name: Build and publish Flex Control Companion v0.6.4

on:
  push:
    branches: [ main, branding-v0.6.4 ]
    paths:
      - '.github/workflows/publish-v0.6.4.yml'
      - 'FlexCompanion.csproj'
      - 'App.xaml'
      - 'App.xaml.cs'
      - 'Assets/**'
      - 'Controls/**'
      - 'Flex/**'
      - 'Services/**'
      - 'Station/**'
      - 'Theme/**'
      - 'ViewModels/**'
      - 'Views/**'
      - 'Pi/**'
      - 'LICENSE'
      - 'NOTICE.md'
      - 'README.md'
  workflow_dispatch:

permissions:
  contents: write

jobs:
  windows:
    name: Windows x64
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'
      - name: Publish Windows x64
        shell: pwsh
        run: |
          $ErrorActionPreference = 'Stop'
          dotnet restore .\\FlexCompanion.csproj
          if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
          dotnet publish .\\FlexCompanion.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\\dist\\windows-x64
          if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
          Compress-Archive -Path .\\dist\\windows-x64\\* -DestinationPath .\\dist\\FlexControlCompanion-v0.6.4-windows-x64.zip -Force
      - uses: actions/upload-artifact@v4
        with:
          name: FlexControlCompanion-v0.6.4-windows-x64
          path: dist/FlexControlCompanion-v0.6.4-windows-x64.zip

  raspberry-pi:
    name: Raspberry Pi / Linux ARM64
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'
      - name: Publish Linux ARM64
        shell: bash
        run: |
          set -euxo pipefail
          dotnet restore ./Pi/FlexCompanion.Pi.csproj
          dotnet publish ./Pi/FlexCompanion.Pi.csproj -c Release -r linux-arm64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./dist/pi-arm64
          tar -C ./dist/pi-arm64 -czf ./dist/FlexControlCompanion-v0.6.4-pi-arm64.tar.gz .
      - uses: actions/upload-artifact@v4
        with:
          name: FlexControlCompanion-v0.6.4-pi-arm64
          path: dist/FlexControlCompanion-v0.6.4-pi-arm64.tar.gz

  release:
    name: GitHub release
    if: github.ref == 'refs/heads/main'
    needs: [windows, raspberry-pi]
    runs-on: ubuntu-latest
    steps:
      - uses: actions/download-artifact@v4
        with:
          path: release-assets
          merge-multiple: true
      - name: Publish Flex Control Companion v0.6.4
        env:
          GH_TOKEN: ${{ github.token }}
        shell: bash
        run: |
          set -euxo pipefail
          RELEASE_NOTES=$(cat <<'EOF'
          Flex Control Companion v0.6.4

          ## Branding update

          - Product name changed from **Flex Companion** to **Flex Control Companion**.
          - New **FCC** application icon replaces the previous **CC** branding.
          - Windows and Raspberry Pi UI, documentation, desktop entries and package metadata use the new name.
          - Internal `FlexCompanion` namespaces, executable name and settings path remain unchanged in this release for compatibility.
          - Includes all v0.6.3 TX controls and AetherSDR TX RN2 functionality.

          ## Downloads

          - **Windows x64** — `FlexControlCompanion-v0.6.4-windows-x64.zip`
          - **Raspberry Pi / Linux ARM64** — `FlexControlCompanion-v0.6.4-pi-arm64.tar.gz`

          Both builds are self-contained and do not require a separate .NET runtime.

          GPLv3. AetherSDR attribution retained.
          EOF
          )

          if gh release view v0.6.4 --repo \"$GITHUB_REPOSITORY\" >/dev/null 2>&1; then
            for asset in FlexControlCompanion-v0.6.4-windows-x64.zip FlexControlCompanion-v0.6.4-pi-arm64.tar.gz; do
              gh release delete-asset v0.6.4 \"$asset\" --yes --repo \"$GITHUB_REPOSITORY\" 2>/dev/null || true
            done
            gh release edit v0.6.4 --repo \"$GITHUB_REPOSITORY\" --title \"Flex Control Companion v0.6.4\" --notes \"$RELEASE_NOTES\"
            gh release upload v0.6.4 release-assets/* --repo \"$GITHUB_REPOSITORY\"
          else
            gh release create v0.6.4 release-assets/* --repo \"$GITHUB_REPOSITORY\" --target \"$GITHUB_SHA\" --title \"Flex Control Companion v0.6.4\" --notes \"$RELEASE_NOTES\"
          fi
""", encoding='utf-8', newline='\n')

Path('CHANGES-v0.6.4.md').write_text("""# Flex Control Companion v0.6.4

## Branding

- Renames the user-facing application from **Flex Companion** to **Flex Control Companion**.
- Replaces the previous **CC** logo with a new **FCC** radio-wave icon.
- Updates Windows, Raspberry Pi/touch, documentation, desktop entries and release packaging to the new branding.
- Keeps the internal `FlexCompanion` assembly/namespace and settings paths unchanged for compatibility.

## Functionality

- Retains the v0.6.3 TX tab: RF power, microphone gain, PROC enable/mode and live FLEX TX status.
- Retains AetherSDR TX RN2 control through the local opt-in AutomationServer.

GPLv3. AetherSDR attribution retained.
""", encoding='utf-8', newline='\n')

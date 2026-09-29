from pathlib import Path

TEXT_EXTS = {'.md', '.cs', '.xaml', '.axaml', '.csproj', '.sh', '.ps1', '.desktop', '.bat'}

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

Path('CHANGES-v0.6.4.md').write_text("""# Flex Control Companion v0.6.4

## Branding

- Renames the user-facing application from **Flex Companion** to **Flex Control Companion**.
- Replaces the previous **CC** logo with a new **FCC** radio-wave icon.
- Updates Windows, Raspberry Pi/touch, documentation and desktop entries to the new branding.
- Keeps the internal `FlexCompanion` assembly/namespace and settings paths unchanged for compatibility.

## Functionality

- Retains the v0.6.3 TX tab: RF power, microphone gain, PROC enable/mode and live FLEX TX status.
- Retains AetherSDR TX RN2 control through the local opt-in AutomationServer.

GPLv3. AetherSDR attribution retained.
""", encoding='utf-8', newline='\n')

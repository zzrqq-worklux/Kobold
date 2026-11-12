# kobold-config skill

This folder is a portable skill for configuring and diagnosing a running
Kobold installation through its local control CLI. It is self-contained: copy
the whole `kobold-config` directory into your agent's skills directory and load
`SKILL.md` from there.

## Requirements

- Kobold is installed, or the repository is checked out.
- The control client `koboldctl.ps1` is available. It ships in the Kobold
  install directory and is also kept at `tools\koboldctl.ps1` in the
  repository.
- PowerShell 5.1 or newer.

The client connects to a named pipe owned by the current Windows user and
session. There is no HTTP endpoint, no remote access, and no separate service
to install.

## Find the CLI

From this folder, run the bundled locator:

```powershell
$ctl = & .\scripts\find-koboldctl.ps1
```

It checks, in order:

1. An explicit `-Path` file or folder.
2. `%LocalAppData%\Programs\Kobold\koboldctl.ps1`.
3. `tools\koboldctl.ps1` three levels above this skill's `scripts` folder.
4. `tools\koboldctl.ps1` in the current directory.

It prints the first existing path and exits 1 with a clear message when none
exists. The locator only finds the CLI; all commands and validation live in
`koboldctl.ps1`.

## Use

Read `SKILL.md` for the workflow, safety rules and examples. Start with:

```powershell
& $ctl status --json
& $ctl config describe --json
& $ctl config get --summary --json
```

All writes go through the CLI. Configuration writes are revision-guarded, and
destructive commands require `--yes`.

## Contents

- `SKILL.md` - the skill instructions, safety rules and command examples.
- `references/config-schema.md` - config fields, widget kinds, groups and
  FileSearch settings.
- `references/troubleshooting.md` - search, config recovery, widgets/groups,
  theme/language and startup problems.
- `scripts/find-koboldctl.ps1` - the CLI locator.

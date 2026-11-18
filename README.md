# vTerm

A terminal for Windows 11 in your vShell theme - part of the vSuite (vShell, vXplorer, vView).

- PowerShell, PowerShell 7, Command Prompt, WSL and SSH in tabs (Windows' ConPTY; the terminal is libghostty-vt, Ghostty's engine, drawn by vTerm)
- Saved SSH connections in folders, quick connect, import from `~/.ssh/config`, saved passwords kept encrypted for your Windows account
- A files panel (SFTP) for the server you're on: browse, upload by dropping files, download, open, rename, delete
- Tabs move between windows with their shells still running
- Follows your vShell theme (colours, opacity, blur) as you change it

Install: `vTerm-setup.exe` from https://vsuite.vrsion.com (or vShell's Settings > Updates > vSuite apps). Needs Windows 10 2004 or
newer and, for SSH, Windows' OpenSSH Client.

Command line: `vTerm.exe [--new-window] [--profile NAME] [--dir FOLDER] [--run COMMAND [--title TITLE]] [--ssh user@host[:port]]
[--host HOST --name NAME --user USER [--run COMMAND]]` - with vTerm already running, it opens there.

## Licence

Copyright (C) 2026 Steven Van Teylingen (version-au). vTerm is free software: you can redistribute it and/or modify it under the terms of the
GNU General Public License, version 3 - see [LICENSE](LICENSE). It comes with no warranty. The shared library vSuite.Core is MIT, and
the parts by others are listed in [THIRD-PARTY.txt](THIRD-PARTY.txt).

Source: https://github.com/version-au/vTerm · Downloads and updates: https://vsuite.vrsion.com

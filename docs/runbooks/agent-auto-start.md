# Runbook: Make the Local Agent auto-start after reboot

Out of the box, `dotnet cortexplexus-agent watch` runs as a regular
foreground / `nohup` process. Closing the terminal is fine (the agent
is detached), but a **machine reboot** — or logout on Windows without a
persistent session — kills it. This runbook wires the agent to a
supervisor so it comes back on its own.

Pick the section that matches your OS.

---

## Linux / macOS — systemd user unit

Works on any distro with systemd ≥ 232 (Debian 10+, Ubuntu 18.04+,
Fedora 25+, Arch). For desktop-class machines the user unit is the
right scope: it starts when the user logs in, survives terminal close,
no `sudo` required. For headless servers, enable lingering so the unit
starts at boot without a login:

```bash
sudo loginctl enable-linger "$USER"
```

Create the unit file. Replace `<NAME>`, `<PATH>`, `<SERVER>` with your
project and server values, or use `%i` + a parametric unit as shown.

```bash
mkdir -p ~/.config/systemd/user

cat > ~/.config/systemd/user/cortexplexus-agent@.service <<'EOF'
[Unit]
Description=CortexPlexus Local Agent — watching project %i
Documentation=https://github.com/DT-Tuan/cortexplexus
Wants=network-online.target
After=network-online.target

[Service]
Type=simple
# Environment file per-project: ~/.config/cortexplexus-agent-<name>.env
EnvironmentFile=%h/.config/cortexplexus-agent-%i.env
ExecStart=/usr/bin/env dotnet %h/.cortexplexus/agent/cortexplexus-agent.dll watch ${PROJECT_PATH} --server ${SERVER_URL} --name %i
Restart=on-failure
RestartSec=15s
# Journal capture with tag so `journalctl --user -t cortexplexus-agent` filters cleanly
SyslogIdentifier=cortexplexus-agent-%i
# Hard limits — trigger a restart rather than hang the host on runaway memory
MemoryMax=2G
TasksMax=256

[Install]
WantedBy=default.target
EOF
```

Per-project env file (one per `<name>`):

```bash
cat > ~/.config/cortexplexus-agent-myproject.env <<'EOF'
PROJECT_PATH=/home/alice/code/myproject
SERVER_URL=http://192.168.50.14:8080
EOF
chmod 600 ~/.config/cortexplexus-agent-myproject.env   # in case SERVER_URL holds auth
```

Enable and start:

```bash
systemctl --user daemon-reload
systemctl --user enable --now cortexplexus-agent@myproject.service
```

### Check status

```bash
systemctl --user status cortexplexus-agent@myproject.service
journalctl --user -u cortexplexus-agent@myproject.service -f
```

### Stop / disable

```bash
systemctl --user stop    cortexplexus-agent@myproject.service
systemctl --user disable cortexplexus-agent@myproject.service
```

### Multi-project

Each project is its own instance:

```bash
systemctl --user enable --now cortexplexus-agent@frontend.service
systemctl --user enable --now cortexplexus-agent@backend.service
```

---

## Windows — Task Scheduler

PowerShell 5+ (shipped with Windows 10/11). Runs under the current user
at logon. For headless Windows Server, use the service wrapper in the
last section instead.

Save this to `agent-task.ps1` and run it **once** as the user you want
the agent running as:

```powershell
$name      = "myproject"
$path      = "C:\Users\Alice\code\myproject"
$server    = "http://192.168.50.14:8080"
$agentDll  = "$env:USERPROFILE\.cortexplexus\agent\cortexplexus-agent.dll"
$logPath   = "$env:USERPROFILE\.cortexplexus\agent-$name.log"

$action = New-ScheduledTaskAction `
    -Execute "dotnet.exe" `
    -Argument "`"$agentDll`" watch `"$path`" --server $server --name $name" `
    -WorkingDirectory $env:USERPROFILE

# Log on of ANY user of this machine that matches $env:USERNAME
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME

$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) `
    -ExecutionTimeLimit ([TimeSpan]::Zero)    # never time out

Register-ScheduledTask `
    -TaskName "CortexPlexus-Agent-$name" `
    -Description "CortexPlexus Local Agent watching $path" `
    -Action $action -Trigger $trigger -Settings $settings `
    -User $env:USERNAME -RunLevel Limited
```

### Check status / logs

```powershell
Get-ScheduledTask -TaskName "CortexPlexus-Agent-myproject" | Get-ScheduledTaskInfo
Get-Content "$env:USERPROFILE\.cortexplexus\agent-myproject.log" -Wait
```

### Disable

```powershell
Unregister-ScheduledTask -TaskName "CortexPlexus-Agent-myproject" -Confirm:$false
```

---

## Headless Windows Server — service via NSSM

If you run the agent on a server that may have no interactive logon,
Task Scheduler `-AtLogOn` won't fire. Install [NSSM](https://nssm.cc)
and wrap the agent as a service:

```cmd
nssm install CortexPlexusAgent-myproject ^
  "C:\Program Files\dotnet\dotnet.exe" ^
  "\"%USERPROFILE%\.cortexplexus\agent\cortexplexus-agent.dll\" watch \"C:\srv\myproject\" --server http://192.168.50.14:8080 --name myproject"

nssm set CortexPlexusAgent-myproject AppStdout C:\ProgramData\cortexplexus-agent\myproject.stdout.log
nssm set CortexPlexusAgent-myproject AppStderr C:\ProgramData\cortexplexus-agent\myproject.stderr.log
nssm set CortexPlexusAgent-myproject AppRotateFiles 1
nssm set CortexPlexusAgent-myproject AppRotateBytes 10485760
nssm set CortexPlexusAgent-myproject Start SERVICE_AUTO_START

nssm start CortexPlexusAgent-myproject
```

---

## macOS LaunchAgent (alternative to systemd)

Drop into `~/Library/LaunchAgents/com.dt-tuan.cortexplexus-agent.myproject.plist`:

```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
  "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>Label</key><string>com.dt-tuan.cortexplexus-agent.myproject</string>
  <key>ProgramArguments</key>
  <array>
    <string>/usr/local/share/dotnet/dotnet</string>
    <string>/Users/alice/.cortexplexus/agent/cortexplexus-agent.dll</string>
    <string>watch</string>
    <string>/Users/alice/code/myproject</string>
    <string>--server</string><string>http://192.168.50.14:8080</string>
    <string>--name</string><string>myproject</string>
  </array>
  <key>RunAtLoad</key><true/>
  <key>KeepAlive</key><true/>
  <key>StandardOutPath</key><string>/Users/alice/.cortexplexus/agent-myproject.log</string>
  <key>StandardErrorPath</key><string>/Users/alice/.cortexplexus/agent-myproject.log</string>
</dict></plist>
```

Load + unload:

```bash
launchctl load   ~/Library/LaunchAgents/com.dt-tuan.cortexplexus-agent.myproject.plist
launchctl unload ~/Library/LaunchAgents/com.dt-tuan.cortexplexus-agent.myproject.plist
```

---

## Verifying auto-start works

1. Reboot the host (or log out and back in for user-scoped units).
2. Wait ~30 seconds.
3. From any MCP client: `ListRepositories()` — your project should still
   show a recent `Last indexed` and `Health: OK`.
4. Edit a file → within ~5 seconds the agent log should show `Detected 1
   changed files, indexing...`. If not, the supervisor didn't restart
   the agent — check the status / journalctl / event log.

---

## VS Code — per-workspace auto-start on folder open

If you open your project in VS Code and don't want a reboot-level
supervisor, drop a `tasks.json` snippet into the workspace so opening
the folder launches the agent automatically. This is the **lowest-cost
fix** for the common "I forgot to start the agent and now my index is
stale" scenario.

Create (or append to) `.vscode/tasks.json` **inside the project you
want indexed** (not the CortexPlexus repo itself):

```jsonc
{
  "version": "2.0.0",
  "tasks": [
    {
      "label": "CortexPlexus: start watch",
      "type": "shell",
      "command": "dotnet",
      "args": [
        "${env:HOME}/.cortexplexus/agent/cortexplexus-agent.dll",
        "watch",
        "${workspaceFolder}",
        "--server", "http://192.168.50.14:8080",
        "--name",  "${workspaceFolderBasename}"
      ],
      "isBackground": true,
      "runOptions": { "runOn": "folderOpen" },
      "presentation": {
        "reveal": "silent",
        "panel": "dedicated",
        "showReuseMessage": false,
        "close": false
      },
      "problemMatcher": []
    }
  ]
}
```

On Windows, replace `${env:HOME}` with `${env:USERPROFILE}` and
`http://...:8080` with your actual server URL (or leave as detected by
ActivateAgent).

### What this gives you

- Opening the folder (any workspace reload) starts the agent in a
  dedicated silent terminal.
- The agent attaches to the server, computes a SHA-256 diff of the
  working tree, and re-syncs only the files that changed since the
  previous run — usually seconds to a minute.
- Closing VS Code stops the agent (task terminates with the editor).
- First run asks for trust on the task; subsequent opens are automatic.

### If multiple people share the project

Commit the `tasks.json` so the convention travels with the code.
Teammates who don't use CortexPlexus can ignore the task; it only
starts when they open the workspace and VS Code surfaces a one-time
trust prompt. The task has no side effects beyond the local agent.

### Limitation — VS Code only

This pattern is VS Code-specific. For other editors see the
[systemd / Windows Task Scheduler / NSSM / macOS LaunchAgent](#linux--macos--systemd-user-unit)
sections above — those are editor-agnostic but reboot-level.

---

## See also

- [`deployment.md`](deployment.md) — initial deploy of CortexPlexus server
- [`maintenance.md`](maintenance.md) — disk & Docker housekeeping

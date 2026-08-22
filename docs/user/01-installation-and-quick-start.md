# 1. Installation and quick start

[User manual](README.md)

NuTestServer currently ships from this repository. The CLI tool is locally
packable but is not published to NuGet.org. Run these commands from the repository
root. The examples use cross-platform PowerShell 7 (`pwsh`).

## Pack and install the local tool

The example uses isolated output and installation directories.

<!-- example-id: user-01-pack-install; evidence: executable -->
```powershell
dotnet pack .\src\NuGet.TestServer.Cli\NuGet.TestServer.Cli.csproj --configuration Release --output "{{ARTIFACTS}}"
dotnet tool install --tool-path "{{TOOLS}}" NuGet.TestServer.Cli --add-source "{{ARTIFACTS}}" --version 1.0.0
```

## Start an isolated server

Use a dedicated storage directory for every test run. Interactive automation may
use `--port 0` and read the selected loopback URL from output; this example
substitutes an ephemeral fixed port so its readiness probe can execute unchanged.

The executable example supervises the process, waits for readiness, and always
stops it. Interactive use can run only the `start` command and stop with
**Ctrl+C**.

<!-- example-id: user-01-start; evidence: executable -->
```powershell
$server = Start-Process "{{TOOL_COMMAND}}" -ArgumentList @(
  "start", "--port", "{{PORT}}", "--storage", "{{STORAGE}}"
) -RedirectStandardOutput "{{OUTPUT_FILE}}" -PassThru
try {
  foreach ($attempt in 1..100) {
    if ((Test-Path "{{OUTPUT_FILE}}") -and
        (Get-Content "{{OUTPUT_FILE}}" -Raw) -match "Vulnerabilities:") { break }
    Start-Sleep -Milliseconds 100
  }
  Get-Content "{{OUTPUT_FILE}}"
  (Invoke-WebRequest "{{BASE_URL}}/health/ready").StatusCode
} finally {
  if (-not $server.HasExited) { Stop-Process -Id $server.Id }
  $server | Wait-Process
}
```

Startup prints the selected source and operational endpoints. Dynamic values are
shown as placeholders:

<!-- example-id: user-01-start-output; evidence: reference -->
```text
Source:      http://127.0.0.1:<port>/v3/index.json
Mode:        Test
Control API: http://127.0.0.1:<port>/__test
Health:      http://127.0.0.1:<port>/__test/health
Liveness:    http://127.0.0.1:<port>/health/live
Readiness:   http://127.0.0.1:<port>/health/ready
Storage:     <absolute-storage-path>
Vulnerabilities: <timestamp> (<snapshot-id>)
```

In another terminal, verify readiness with the printed base URL:

<!-- example-id: user-01-readiness; evidence: executable -->
```powershell
(Invoke-WebRequest "{{BASE_URL}}/health/ready").StatusCode
```

Expected output is `200`. The service index is `{{BASE_URL}}/v3/index.json`.

## Stop and clean up

Press **Ctrl+C** in the server terminal, then remove only the paths created for
this example.

<!-- example-id: user-01-cleanup; evidence: executable -->
```powershell
Remove-Item -Recurse -Force "{{ARTIFACTS}}","{{TOOLS}}","{{STORAGE}}" -ErrorAction SilentlyContinue
```

## Security note

The default test profile is anonymous, uses loopback HTTP, and allows package
mutation and test controls. Do not expose it to untrusted networks. See
[Chapter 4](04-authentication-and-production.md) before using non-loopback
listeners or persistent shared storage.

**Previous:** [User manual](README.md)  
**Next:** [NuGet package workflows](02-package-workflows.md)

param([string]$OutDir = '')
$ErrorActionPreference = 'Stop'
# Builds with the C# compiler that ships with Windows (.NET Framework 4.x); nothing needs to be installed.
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw "csc.exe not found: $compiler" }
if (!$OutDir) { $OutDir = Join-Path $PSScriptRoot 'bin' }
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
$common = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/codepage:65001', '/reference:System.Net.Http.dll', '/reference:System.Web.Extensions.dll', '/reference:System.Security.dll', '/reference:System.Management.dll')
# RemoteCli.exe: the window, with the terminal agent inside.
$agentSources = 'RemoteCliAgent','Antigravity','Chats','ClaudePermissions','DesktopAdapters','Ways','Outbox','Running','Stats','Schedules','Chains','CodexDesktopControl','CodexSessions','SessionLocks','Transcripts','Reach','Marks','ClaudeRelay','Folders','FileTransfers','Removal','CodexQueue','Tools','AcpChat','Quotas','Teams','Peers' | ForEach-Object { Join-Path $PSScriptRoot ($_ + '.cs') }
$windowSources = 'RemoteCliApp','ToolInstaller','LanNetwork','Controls','Notice','QrCode','AutoUpdater','Relays','Remote' | ForEach-Object { Join-Path $PSScriptRoot ($_ + '.cs') }
& $compiler @common "/out:$(Join-Path $OutDir 'RemoteCli.exe')" /main:RemoteCli.App /reference:System.Windows.Forms.dll /reference:System.Drawing.dll @agentSources @windowSources
if ($LASTEXITCODE -ne 0) { throw 'RemoteCli.exe: compilation failed' }
# RemoteCliAgent.exe: the agent alone, without a window, for a computer that uses a relay elsewhere.
& $compiler @common "/out:$(Join-Path $OutDir 'RemoteCliAgent.exe')" @agentSources
if ($LASTEXITCODE -ne 0) { throw 'RemoteCliAgent.exe: compilation failed' }
Get-ChildItem -LiteralPath $OutDir -Filter '*.exe' | ForEach-Object { '{0}  {1} bytes' -f $_.Name, $_.Length }

param([string]$OutDir = '')
$ErrorActionPreference = 'Stop'
# Builds with the C# compiler that ships with Windows (.NET Framework 4.x); nothing needs to be installed.
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw "csc.exe not found: $compiler" }
$icon = Join-Path $PSScriptRoot 'remote-ai.ico'
if (!(Test-Path -LiteralPath $icon)) { throw "application icon not found: $icon" }
if (!$OutDir) { $OutDir = Join-Path $PSScriptRoot 'bin' }
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

# Pinned Microsoft.Web.WebView2 NuGet package (verified by SHA-256) for the lightweight native desktop console shell.
$repoRoot = Split-Path -Parent $PSScriptRoot
$cacheDir = Join-Path $repoRoot '.cache'
New-Item -ItemType Directory -Path $cacheDir -Force | Out-Null
$wvVer = '1.0.2903.40'
$wvSha = 'ef128016dd1e51c59178c827ed5b8aa3322c57afa8675d930f8109505542ad74'
$wvPkg = Join-Path $cacheDir "Microsoft.Web.WebView2.$wvVer.nupkg"
$wvDir = Join-Path $cacheDir "webview2-$wvVer"
$wvCore = Join-Path $wvDir 'Microsoft.Web.WebView2.Core.dll'
$wvWinForms = Join-Path $wvDir 'Microsoft.Web.WebView2.WinForms.dll'
$wvLoader = Join-Path $wvDir 'WebView2Loader.dll'
if (!(Test-Path -LiteralPath $wvCore) -or !(Test-Path -LiteralPath $wvWinForms) -or !(Test-Path -LiteralPath $wvLoader)) {
    if (!(Test-Path -LiteralPath $wvPkg)) {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -UseBasicParsing -Uri "https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/$wvVer/microsoft.web.webview2.$wvVer.nupkg" -OutFile $wvPkg
    }
    $actualSha = (Get-FileHash -Algorithm SHA256 -LiteralPath $wvPkg).Hash.ToLowerInvariant()
    if ($actualSha -ne $wvSha) { throw "WebView2 NuGet SHA-256 mismatch: $actualSha" }
    New-Item -ItemType Directory -Path $wvDir -Force | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($wvPkg)
    try {
        $wanted = @{
            'lib/net462/Microsoft.Web.WebView2.Core.dll' = $wvCore
            'lib/net462/Microsoft.Web.WebView2.WinForms.dll' = $wvWinForms
            'runtimes/win-x64/native/WebView2Loader.dll' = $wvLoader
        }
        foreach ($entryName in $wanted.Keys) {
            $entry = $zip.GetEntry($entryName)
            if ($null -eq $entry) { throw "Missing entry in WebView2 package: $entryName" }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $wanted[$entryName], $true)
        }
    } finally { $zip.Dispose() }
}
Copy-Item -LiteralPath $wvCore, $wvWinForms, $wvLoader -Destination $OutDir -Force

$common = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/codepage:65001', "/win32icon:$icon", '/reference:System.Net.Http.dll', '/reference:System.Web.Extensions.dll', '/reference:System.Security.dll', '/reference:System.Management.dll')
# RemoteCli.exe: the window, with the terminal agent and WebView2 desktop console inside.
$agentSources = 'RemoteCliAgent','ProcessText','WorkspaceSnapshot','RelayLink','RouteDiscovery','GitReview','Antigravity','AntigravityDesktop','Chats','ClaudePermissions','DesktopAdapters','Ways','Outbox','Running','Stats','Schedules','Chains','CodexDesktopControl','CodexSessions','SessionLocks','Transcripts','Reach','Marks','Handovers','ClaudeRelay','Folders','FileTransfers','Removal','CodexQueue','Tools','AcpChat','Quotas','CodexApiBalance','Teams','Models','Peers','Delegations','FileSearch','Worktrees','Organizer','OrganizeHistory','OrganizeSource','CodexOrganizer','ClaudeOrganizer','AgyOrganizer','ApiOrganizer','Attachments' | ForEach-Object { Join-Path $PSScriptRoot ($_ + '.cs') }
$windowSources = 'RemoteCliApp','DesktopConsole','TunnelSupervisor','ToolInstaller','LanNetwork','Controls','Notice','QrCode','AutoUpdater','Relays','Remote' | ForEach-Object { Join-Path $PSScriptRoot ($_ + '.cs') }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'RemoteCli.exe.config') -Destination $OutDir -Force
& $compiler @common "/win32manifest:$PSScriptRoot/RemoteCli.manifest" "/resource:$icon,remote-ai.ico" "/out:$(Join-Path $OutDir 'RemoteCli.exe')" /main:RemoteCli.App /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "/reference:$wvCore" "/reference:$wvWinForms" @agentSources @windowSources
if ($LASTEXITCODE -ne 0) { throw 'RemoteCli.exe: compilation failed' }
# RemoteCliAgent.exe: the agent alone, without a window, for a computer that uses a relay elsewhere.
& $compiler @common "/out:$(Join-Path $OutDir 'RemoteCliAgent.exe')" @agentSources
if ($LASTEXITCODE -ne 0) { throw 'RemoteCliAgent.exe: compilation failed' }
Get-ChildItem -LiteralPath $OutDir -Filter '*.exe' | ForEach-Object { '{0}  {1} bytes' -f $_.Name, $_.Length }

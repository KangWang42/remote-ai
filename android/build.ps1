param(
    [string]$Jdk = $env:JAVA_HOME,
    [string]$Sdk = $env:ANDROID_HOME,
    [string]$SecretDir = (Join-Path $HOME '.remote-cli-signing'),
    [switch]$CreateSigningKey
)
# Builds dist/RemoteCli-Android.apk with the Android command-line tools only (no Gradle).
# Needs a JDK 11+ and an Android SDK with build-tools 36.0.0 and platforms/android-36.
# The signing key is created on first use in $SecretDir, outside the repository. Keep it: an app signed with
# another key cannot be installed over this one.
$ErrorActionPreference = 'Stop'
if (!$CreateSigningKey -and (!(Test-Path -LiteralPath (Join-Path $SecretDir 'signing.p12')) -or !(Test-Path -LiteralPath (Join-Path $SecretDir 'signing-password.txt')))) {
    throw 'Existing signing key and password are required. Pass -SecretDir for the release key; use -CreateSigningKey only for a new application identity.'
}
Set-Location -LiteralPath $PSScriptRoot
if (!$Jdk -or !$Sdk) { throw 'Pass -Jdk and -Sdk, or set JAVA_HOME and ANDROID_HOME' }
$buildTools = Join-Path $Sdk 'build-tools/36.0.0'
$androidJar = Join-Path $Sdk 'platforms/android-36/android.jar'
$java = Join-Path $Jdk 'bin/java.exe'
$javac = Join-Path $Jdk 'bin/javac.exe'
foreach ($file in @($java, $javac, $androidJar, (Join-Path $buildTools 'aapt2.exe'))) {
    if (!(Test-Path -LiteralPath $file)) { throw "missing build tool: $file" }
}
$build = Join-Path (Split-Path $PSScriptRoot -Parent) ('.build/android-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$classes = Join-Path $build 'classes'; $generated = Join-Path $build 'generated'; $dex = Join-Path $build 'dex'
$output = Join-Path (Split-Path $PSScriptRoot -Parent) 'dist'
foreach ($directory in @($build, $classes, $generated, $dex, $output)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
function Invoke-Tool([string]$Tool, [string[]]$Arguments) {
    & $Tool @Arguments
    if ($LASTEXITCODE -ne 0) { throw "failed: $(Split-Path $Tool -Leaf), exit code $LASTEXITCODE" }
}
# The QR reader (ZXing core, Apache-2.0) comes from Maven Central once and is checked against a known hash.
$zxing = Join-Path (Split-Path $PSScriptRoot -Parent) '.cache/zxing-core-3.5.3.jar'
if (!(Test-Path -LiteralPath $zxing)) {
    New-Item -ItemType Directory -Path (Split-Path $zxing) -Force | Out-Null
    Invoke-WebRequest -Uri 'https://repo1.maven.org/maven2/com/google/zxing/core/3.5.3/core-3.5.3.jar' -OutFile $zxing -UseBasicParsing
}
if ((Get-FileHash -LiteralPath $zxing -Algorithm SHA256).Hash.ToLower() -ne '8d8064c1636fdaef7189dd9055c7d59950a8940a12f2293956446ec3c109fd82') { throw "unexpected content: $zxing" }
$compiled = Join-Path $build 'resources.zip'
$unsigned = Join-Path $build 'unsigned.apk'
# Bundle the same page sources as the Windows installer. Public relays may still serve an older UI.
$webRoot = Join-Path (Split-Path $PSScriptRoot -Parent) 'web'
$webFiles = @(Get-ChildItem -LiteralPath $webRoot -File -Recurse)
$mime = @{ '.html'='text/html'; '.js'='text/javascript'; '.css'='text/css'; '.woff2'='font/woff2'; '.bcmap'='application/octet-stream'; '.json'='application/json' }
$entries = @($webFiles | ForEach-Object {
    if ($mime.ContainsKey($_.Extension)) {
        $name = $_.FullName.Substring($webRoot.Length + 1).Replace('\', '/')
        if ($name -notmatch '^[a-zA-Z0-9_./-]+$') { throw "Unsupported page asset name: $name" }
        $name + '=' + $mime[$_.Extension]
    }
})
$assetManifest = Join-Path $build 'web-assets.properties'
[System.IO.File]::WriteAllLines($assetManifest, $entries, [System.Text.UTF8Encoding]::new($false))
Invoke-Tool (Join-Path $buildTools 'aapt2.exe') @('compile', '--dir', 'res', '-o', $compiled)
Invoke-Tool (Join-Path $buildTools 'aapt2.exe') @('link', '-o', $unsigned, '-I', $androidJar, '--manifest', 'AndroidManifest.xml', '--java', $generated, $compiled)
$sources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.java' -Recurse | ForEach-Object FullName)
$sources += @(Get-ChildItem -LiteralPath $generated -Filter '*.java' -Recurse | ForEach-Object FullName)
Invoke-Tool $javac (@('-J-Duser.language=en', '-Xlint:-options', '--release', '8', '-encoding', 'UTF-8', '-classpath', "$androidJar;$zxing", '-d', $classes) + $sources)
$classesJar = Join-Path $build 'classes.jar'
Invoke-Tool (Join-Path $Jdk 'bin/jar.exe') @('cf', $classesJar, '-C', $classes, '.')
Invoke-Tool $java @('-cp', (Join-Path $buildTools 'lib/d8.jar'), 'com.android.tools.r8.D8', '--lib', $androidJar, '--min-api', '26', '--output', $dex, $classesJar, $zxing)
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
$zip = [System.IO.Compression.ZipFile]::Open($unsigned, [System.IO.Compression.ZipArchiveMode]::Update)
try {
    [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $dex 'classes.dex'), 'classes.dex') | Out-Null
    [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $assetManifest, 'assets/web-assets.properties') | Out-Null
    foreach ($file in $webFiles) {
        $name = $file.FullName.Substring($webRoot.Length + 1).Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, ('assets/web/' + $name)) | Out-Null
    }
}
finally { $zip.Dispose() }
$aligned = Join-Path $build 'aligned.apk'
Invoke-Tool (Join-Path $buildTools 'zipalign.exe') @('-f', '-p', '4', $unsigned, $aligned)
New-Item -ItemType Directory -Path $SecretDir -Force | Out-Null
$passwordFile = Join-Path $SecretDir 'signing-password.txt'
$keyFile = Join-Path $SecretDir 'signing.p12'
if (!(Test-Path -LiteralPath $passwordFile)) {
    if (Test-Path -LiteralPath $keyFile) { throw 'the signing password is missing; refusing to replace the existing key' }
    $bytes = New-Object byte[] 32
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    [System.IO.File]::WriteAllText($passwordFile, [Convert]::ToBase64String($bytes), [System.Text.UTF8Encoding]::new($false))
}
$previous = $env:REMOTECLI_SIGN_PASSWORD
$env:REMOTECLI_SIGN_PASSWORD = [System.IO.File]::ReadAllText($passwordFile).Trim()
try {
    if (!(Test-Path -LiteralPath $keyFile)) {
        Invoke-Tool (Join-Path $Jdk 'bin/keytool.exe') @('-genkeypair', '-keystore', $keyFile, '-storetype', 'PKCS12', '-alias', 'remotecli', '-keyalg', 'RSA', '-keysize', '3072', '-validity', '10000', '-dname', 'CN=Remote CLI', '-storepass:env', 'REMOTECLI_SIGN_PASSWORD', '-keypass:env', 'REMOTECLI_SIGN_PASSWORD')
    }
    $apk = Join-Path $output 'RemoteCli-Android.apk'
    Invoke-Tool $java @('-jar', (Join-Path $buildTools 'lib/apksigner.jar'), 'sign', '--ks', $keyFile, '--ks-key-alias', 'remotecli', '--ks-pass', 'env:REMOTECLI_SIGN_PASSWORD', '--out', $apk, $aligned)
    Invoke-Tool $java @('-jar', (Join-Path $buildTools 'lib/apksigner.jar'), 'verify', $apk)
    Invoke-Tool (Join-Path $buildTools 'zipalign.exe') @('-c', '4', $apk)
    Remove-Item -LiteralPath ($apk + '.idsig') -ErrorAction SilentlyContinue
    $hash = (Get-FileHash -LiteralPath $apk -Algorithm SHA256).Hash.ToLower()
    Write-Output ("RemoteCli-Android.apk  {0} bytes  sha256 {1}" -f (Get-Item -LiteralPath $apk).Length, $hash)
} finally { $env:REMOTECLI_SIGN_PASSWORD = $previous }

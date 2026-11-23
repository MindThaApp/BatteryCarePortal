param(
    [string]$OutDir = (Join-Path $PSScriptRoot '..\builds\beta1'),
    [string]$Publisher = 'CN=BatteryCarePortal Test'
)
# Sideload test package: x64 Release MSIX signed with a self-signed test certificate whose subject matches the
# manifest Publisher. The committed manifest keeps its placeholder; it is swapped for the build and always restored.
# The certificate and its password stay under certs\ (git-ignored) and are never committed.
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$msbuild = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe'
$signtool = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe'
$project = Join-Path $root 'src\BatteryCarePortal.App\BatteryCarePortal.App.csproj'
$manifestPath = Join-Path $root 'src\BatteryCarePortal.App\Package.appxmanifest'
$certDir = Join-Path $root 'certs'
$pfx = Join-Path $certDir 'test-signing.pfx'
$pwFile = Join-Path $certDir 'test-signing-password.txt'

New-Item -ItemType Directory -Force $certDir, $OutDir | Out-Null

if (-not ((Test-Path $pfx) -and (Test-Path $pwFile))) {
    $plain = [Convert]::ToBase64String([byte[]](1..18 | ForEach-Object { Get-Random -Maximum 256 }))
    Set-Content $pwFile $plain -NoNewline
    $cert = New-SelfSignedCertificate -Type Custom -Subject $Publisher -KeyUsage DigitalSignature `
        -FriendlyName 'Battery Care Portal test' -CertStoreLocation 'Cert:\CurrentUser\My' `
        -NotAfter (Get-Date).AddYears(2) `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    Export-PfxCertificate -Cert $cert -FilePath $pfx -Password (ConvertTo-SecureString $plain -AsPlainText -Force) | Out-Null
    # The private key lives in the .pfx under certs\; drop the store copy.
    $cert | Remove-Item
}
$password = Get-Content $pwFile -Raw

$pkgDir = Join-Path $OutDir 'pkg\'
$original = [IO.File]::ReadAllBytes($manifestPath)
try {
    $text = [Text.Encoding]::UTF8.GetString($original)
    $patched = $text -replace 'Publisher="[^"]*"', "Publisher=`"$Publisher`""
    [IO.File]::WriteAllText($manifestPath, $patched, (New-Object Text.UTF8Encoding($false)))
    & $msbuild $project /restore /t:Build /nr:false /m /v:m /nologo /p:Configuration=Release /p:Platform=x64 `
        /p:MakeTestPackage=true /p:BcpChannel=beta /p:UapAppxPackageBuildMode=SideloadOnly /p:AppxBundle=Never `
        /p:AppxPackageSigningEnabled=false "/p:AppxPackageDir=$pkgDir" 2>&1 | Select-String 'error|Build succeeded'
    if ($LASTEXITCODE -ne 0) { throw "build failed ($LASTEXITCODE)" }
}
finally {
    [IO.File]::WriteAllBytes($manifestPath, $original)
}

$built = Get-ChildItem $pkgDir -Recurse -Filter '*_x64.msix' | Select-Object -First 1
if (-not $built) { throw "No x64 .msix under $pkgDir" }
$msix = Join-Path $OutDir $built.Name
Copy-Item $built.FullName $msix -Force
& $signtool sign /fd SHA256 /f $pfx /p $password $msix 2>&1 | Select-String 'Successfully|error'
if ($LASTEXITCODE -ne 0) { throw "signing failed ($LASTEXITCODE)" }

# Start and the taskbar need every Square44x44Logo size indexed in resources.pri, in plain, unplated and lightunplated forms;
# without them Windows draws the logo on an accent plate or shows the generic icon in the taskbar hover preview.
$makepri = Join-Path (Split-Path $signtool -Parent) 'makepri.exe'
$check = Join-Path ([IO.Path]::GetTempPath()) ('bcp-pri-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $check | Out-Null
try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($msix)
    try {
        $entry = $zip.Entries | Where-Object { $_.FullName -eq 'resources.pri' }
        if (-not $entry) { throw 'resources.pri is missing from the package' }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $check 'resources.pri'))
    }
    finally { $zip.Dispose() }
    $xml = Join-Path $check 'pri.xml'
    & $makepri dump /if (Join-Path $check 'resources.pri') /of $xml /o | Out-Null
    $dump = Get-Content $xml -Raw
    $missing = foreach ($s in 16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256) {
        foreach ($form in '', '_altform-unplated', '_altform-lightunplated') {
            $name = "Square44x44Logo.targetsize-$s$form.png"
            if (-not $dump.Contains($name)) { $name }
        }
    }
    if ($missing) { throw "resources.pri does not index: $($missing -join ', ')" }
    'resources.pri indexes all Square44x44Logo sizes (plain, unplated, lightunplated)'
}
finally { Remove-Item $check -Recurse -Force }

$cer = Join-Path $OutDir 'BatteryCarePortalTest.cer'
$pub = New-Object Security.Cryptography.X509Certificates.X509Certificate2($pfx, $password)
[IO.File]::WriteAllBytes($cer, $pub.Export('Cert'))
Remove-Item $pkgDir -Recurse -Force
"MSIX $msix"
"CER $cer"

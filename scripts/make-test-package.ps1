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

$cer = Join-Path $OutDir 'BatteryCarePortalTest.cer'
$pub = New-Object Security.Cryptography.X509Certificates.X509Certificate2($pfx, $password)
[IO.File]::WriteAllBytes($cer, $pub.Export('Cert'))
Remove-Item $pkgDir -Recurse -Force
"MSIX $msix"
"CER $cer"

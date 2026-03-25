param(
    [string]$BaseUrl = "https://127.0.0.1:63543",
    [string]$WorkspaceRoot = "",
    [switch]$SkipVerification
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($WorkspaceRoot)) {
    $WorkspaceRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
}

function Read-JsonFile {
    param([string]$Path)
    if (-not (Test-Path $Path)) {
        throw "File not found: $Path"
    }
    return Get-Content $Path -Raw | ConvertFrom-Json
}

Write-Host "Fetching mock auth token from $BaseUrl ..."
$auth = (curl.exe -k -s "$BaseUrl/api/dev/mock-auth" | ConvertFrom-Json)
$token = if ($auth.token) { $auth.token } else { $auth.accessToken }
if ([string]::IsNullOrWhiteSpace($token)) {
    throw "Unable to obtain auth token from $BaseUrl/api/dev/mock-auth"
}

$vendorsPath = Join-Path $WorkspaceRoot "vendor-import-output/vendors.json"
$reviewsPath = Join-Path $WorkspaceRoot "vendor-import-output/vendor-reviews.json"
$youthPath = Join-Path $WorkspaceRoot "vendor-import-output/youth-services.json"

Write-Host "Reading import files ..."
$vendorsRaw = Read-JsonFile -Path $vendorsPath
$reviewsRaw = Read-JsonFile -Path $reviewsPath
$youthRaw = Read-JsonFile -Path $youthPath

$vendors = @()
foreach ($v in $vendorsRaw) {
    $vendors += @{
        fingerprint = ($v.Fingerprint + "")
        name = ($v.Name + "")
        categories = @($v.Categories)
        isNeighborAffiliated = [bool]$v.IsNeighborAffiliated
        phone = ($v.Phone + "")
        email = ($v.Email + "")
        website = ($v.Website + "")
        address = ($v.Address + "")
        notes = ($v.Notes + "")
        # Required by current API contract for new vendor creation.
        initialReviewText = "Imported vendor listing."
    }
}

$reviews = @()
foreach ($r in $reviewsRaw) {
    $reviews += @{
        vendorFingerprint = ($r.VendorFingerprint + "")
        authorDisplayName = ($r.ReferrerName + "")
        reviewText = ($r.ReviewText + "")
    }
}

$youth = @()
foreach ($y in $youthRaw) {
    $youth += @{
        name = ($y.Name + "")
        services = @($y.Services)
        bornYear = $y.BornYear
        phone = ($y.Phone + "")
        contactMethod = if ((($y.ContactMethod + "") -ieq "Call")) { 0 } else { 1 }
        email = ($y.Email + "")
        address = ($y.Address + "")
        parentNote = ($y.ParentNote + "")
    }
}

$payload = @{
    vendors = $vendors
    reviews = $reviews
    youthServices = $youth
} | ConvertTo-Json -Depth 8

$tmp = Join-Path $env:TEMP ("cohad-seed-import-all-" + [Guid]::NewGuid().ToString("n") + ".json")
Set-Content -Path $tmp -Value $payload -Encoding UTF8

try {
    Write-Host "Posting seed payload to $BaseUrl/api/vendor-import/seed ..."
    $resultJson = curl.exe -k -s -X POST "$BaseUrl/api/vendor-import/seed" `
        -H "Authorization: Bearer $token" `
        -H "Content-Type: application/json" `
        --data-binary "@$tmp"
    $result = $resultJson | ConvertFrom-Json
    Write-Host ("Seed import result: " + ($result | ConvertTo-Json -Compress))

    if (-not $SkipVerification) {
        $vendorsApi = (curl.exe -k -s "$BaseUrl/api/vendors" -H "Authorization: Bearer $token") | ConvertFrom-Json
        $youthApi = (curl.exe -k -s "$BaseUrl/api/youthservices" -H "Authorization: Bearer $token") | ConvertFrom-Json
        $neighborTrue = ($vendorsApi | Where-Object { $_.isNeighborAffiliated -eq $true }).Count
        $neighborFalse = ($vendorsApi | Where-Object { $_.isNeighborAffiliated -ne $true }).Count

        Write-Host ("API vendor count: " + $vendorsApi.Count)
        Write-Host ("API youth service count: " + $youthApi.Count)
        Write-Host ("Neighbor affiliated true: " + $neighborTrue + ", false: " + $neighborFalse)
    }
}
finally {
    Remove-Item $tmp -ErrorAction SilentlyContinue
}

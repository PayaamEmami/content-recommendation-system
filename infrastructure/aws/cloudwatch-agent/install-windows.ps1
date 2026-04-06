param(
    [string]$Region = "us-west-2",
    [string]$ConfigTemplatePath = ".\windows-config.json",
    [string]$RenderedConfigPath = "C:\ProgramData\Amazon\AmazonCloudWatchAgent\crs-config.json"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $ConfigTemplatePath)) {
    throw "CloudWatch Agent config template not found: $ConfigTemplatePath"
}

$configDirectory = Split-Path -Parent $RenderedConfigPath
New-Item -ItemType Directory -Path $configDirectory -Force | Out-Null

$template = Get-Content -LiteralPath $ConfigTemplatePath -Raw
$rendered = $template.Replace('${AWS_REGION}', $Region)

# Windows PowerShell writes a UTF-8 BOM with Set-Content -Encoding UTF8, and
# the CloudWatch Agent config translator can choke on that prefix.
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($RenderedConfigPath, $rendered, $utf8NoBom)

$agentRoot = "C:\Program Files\Amazon\AmazonCloudWatchAgent"
$controlScript = Join-Path $agentRoot "amazon-cloudwatch-agent-ctl.ps1"

if (-not (Test-Path -LiteralPath $controlScript)) {
    throw "CloudWatch Agent control script not found at $controlScript. Install the CloudWatch Agent first."
}

& $controlScript -a fetch-config -m onPremise -s -c "file:$RenderedConfigPath"
Write-Output "CloudWatch Agent configuration applied from $RenderedConfigPath"

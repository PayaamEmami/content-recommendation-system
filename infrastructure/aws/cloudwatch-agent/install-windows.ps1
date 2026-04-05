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
$rendered | Set-Content -LiteralPath $RenderedConfigPath -Encoding UTF8

$agentRoot = "C:\Program Files\Amazon\AmazonCloudWatchAgent"
$controlScript = Join-Path $agentRoot "amazon-cloudwatch-agent-ctl.ps1"

if (-not (Test-Path -LiteralPath $controlScript)) {
    throw "CloudWatch Agent control script not found at $controlScript. Install the CloudWatch Agent first."
}

& $controlScript -a fetch-config -m onPremise -s -c "file:$RenderedConfigPath"
Write-Output "CloudWatch Agent configuration applied from $RenderedConfigPath"

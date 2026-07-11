param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("ingestion", "x-ingestion", "feed", "reindex", "sync-index")]
    [string]$JobName,
    [switch]$Interactive
)

$ErrorActionPreference = "Stop"
$repoRoot = $PSScriptRoot
$runId = [Guid]::NewGuid().ToString("N")
$startedAt = Get-Date
$executionEnvironment = "local"
$serviceName = "crs-jobs"
$metricsNamespace = "CRS/Application"
$logRoot = Join-Path $env:ProgramData "CRS\observability\jobs"
$jobLogDirectory = Join-Path $logRoot $JobName
$jobLogPath = Join-Path $jobLogDirectory ("{0}.jsonl" -f (Get-Date -Format "yyyy-MM-dd"))

New-Item -ItemType Directory -Path $jobLogDirectory -Force | Out-Null
Get-ChildItem -Path $jobLogDirectory -Filter "*.jsonl" -ErrorAction SilentlyContinue |
    Where-Object { $_.LastWriteTimeUtc -lt (Get-Date).ToUniversalTime().AddDays(-30) } |
    Remove-Item -Force -ErrorAction SilentlyContinue

function Write-StructuredLog {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Level,
        [Parameter(Mandatory = $true)]
        [string]$EventName,
        [Parameter(Mandatory = $true)]
        [string]$Message,
        [hashtable]$Properties = @{},
        [hashtable]$Metric = $null
    )

    $payload = [ordered]@{
        timestamp = (Get-Date).ToUniversalTime().ToString("O")
        level = $Level
        service = $serviceName
        environment = if ($env:Observability__Environment) { $env:Observability__Environment } else { "Production" }
        executionEnvironment = $executionEnvironment
        host = $env:COMPUTERNAME
        event = $EventName
        message = $Message
        jobName = $JobName
        jobRunId = $runId
    }

    foreach ($key in $Properties.Keys) {
        $payload[$key] = $Properties[$key]
    }

    if ($Metric) {
        $dimensionNames = @("Service", "Environment", "ExecutionEnvironment", "JobName", "Operation", "Outcome")
        $payload["_aws"] = @{
            Timestamp = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
            CloudWatchMetrics = @(
                @{
                    Namespace = $metricsNamespace
                    Dimensions = @($dimensionNames)
                    Metrics = @(
                        @{
                            Name = $Metric.Name
                            Unit = $Metric.Unit
                        }
                    )
                }
            )
        }
        $payload["Service"] = $serviceName
        $payload["Environment"] = if ($env:Observability__Environment) { $env:Observability__Environment } else { "Production" }
        $payload["ExecutionEnvironment"] = $executionEnvironment
        $payload["JobName"] = $JobName
        $payload["Operation"] = $Metric.Operation
        $payload["Outcome"] = $Metric.Outcome
        $payload[$Metric.Name] = $Metric.Value
    }

    $json = $payload | ConvertTo-Json -Compress -Depth 8
    Write-JobLogLine -Path $jobLogPath -Line $json
    Write-Output $json
}

function Write-JobLogLine {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Line
    )

    # Open the file with FileShare.ReadWrite so concurrent writers (e.g. an
    # interactive run and a leftover scheduled task firing at the same time)
    # can both append without crashing each other. Wrap in a brief retry to
    # cover momentary handle contention. Logging failures must never abort
    # the job itself.
    $maxAttempts = 5
    $attempt = 0
    while ($attempt -lt $maxAttempts) {
        try {
            $stream = [System.IO.File]::Open(
                $Path,
                [System.IO.FileMode]::Append,
                [System.IO.FileAccess]::Write,
                [System.IO.FileShare]::ReadWrite)
            try {
                $writer = New-Object System.IO.StreamWriter($stream, [System.Text.Encoding]::UTF8)
                try {
                    $writer.WriteLine($Line)
                    $writer.Flush()
                }
                finally {
                    $writer.Dispose()
                }
            }
            finally {
                $stream.Dispose()
            }
            return
        }
        catch [System.IO.IOException] {
            $attempt++
            if ($attempt -ge $maxAttempts) {
                Write-Warning ("Failed to write log line after {0} attempts: {1}" -f $attempt, $_.Exception.Message)
                return
            }
            Start-Sleep -Milliseconds (50 * $attempt)
        }
        catch {
            Write-Warning ("Unexpected error writing log line: {0}" -f $_.Exception.Message)
            return
        }
    }
}

function Write-MetricEvent {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [double]$Value,
        [Parameter(Mandatory = $true)]
        [string]$Unit,
        [Parameter(Mandatory = $true)]
        [string]$Operation,
        [Parameter(Mandatory = $true)]
        [string]$Outcome,
        [string]$Message = "metric.emitted",
        [hashtable]$Properties = @{}
    )

    Write-StructuredLog -Level "Information" -EventName $Name -Message $Message -Properties $Properties -Metric @{
        Name = $Name
        Value = $Value
        Unit = $Unit
        Operation = $Operation
        Outcome = $Outcome
    }
}

function Write-ProcessOutputLog {
    param(
        [Parameter(Mandatory = $true)]
        [object]$OutputLine,
        [Parameter(Mandatory = $true)]
        [int]$LineNumber,
        [Parameter(Mandatory = $true)]
        [string]$Stream
    )

    $line = $OutputLine.ToString()
    if ([string]::IsNullOrWhiteSpace($line)) {
        return
    }

    $level = if ($line -match '(?i)\b(error|fail(ed|ure)?|exception)\b') {
        "Error"
    } elseif ($line -match '(?i)\b(warn|warning)\b') {
        "Warning"
    } else {
        "Information"
    }

    Write-StructuredLog -Level $level -EventName "job.process.output" -Message $line -Properties @{
        lineNumber = $LineNumber
        stream = $Stream
    }
}

function Invoke-JobProcess {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [switch]$StreamToConsole
    )

    $arguments = @("run", "--no-launch-profile", "--configuration", "Release", "--project", "src/Crs.Jobs", "--", $Name)

    if ($StreamToConsole) {
        Push-Location $repoRoot
        try {
            & dotnet @arguments
            return $LASTEXITCODE
        }
        finally {
            Pop-Location
        }
    }

    $stdoutPath = Join-Path $jobLogDirectory ("{0}-{1}-stdout.log" -f $runId, $Name)
    $stderrPath = Join-Path $jobLogDirectory ("{0}-{1}-stderr.log" -f $runId, $Name)

    try {
        $process = Start-Process `
            -FilePath "dotnet" `
            -ArgumentList $arguments `
            -WorkingDirectory $repoRoot `
            -NoNewWindow `
            -RedirectStandardOutput $stdoutPath `
            -RedirectStandardError $stderrPath `
            -Wait `
            -PassThru

        $lineNumber = 0
        if (Test-Path -LiteralPath $stdoutPath) {
            Get-Content -LiteralPath $stdoutPath | ForEach-Object {
                $lineNumber++
                Write-ProcessOutputLog -OutputLine $_ -LineNumber $lineNumber -Stream "stdout"
            }
        }

        if (Test-Path -LiteralPath $stderrPath) {
            Get-Content -LiteralPath $stderrPath | ForEach-Object {
                $lineNumber++
                Write-ProcessOutputLog -OutputLine $_ -LineNumber $lineNumber -Stream "stderr"
            }
        }

        return $process.ExitCode
    }
    finally {
        Remove-Item -LiteralPath $stdoutPath, $stderrPath -Force -ErrorAction SilentlyContinue
    }
}

function Import-LocalJobSecrets {
    $secretsFile = Join-Path $repoRoot "infrastructure\aws\secrets.env"
    if (-not (Test-Path -LiteralPath $secretsFile)) {
        return
    }

    Write-StructuredLog -Level "Information" -EventName "job.secrets.loaded" -Message "Loading local job secrets from secrets.env" -Properties @{
        secretsFile = $secretsFile
    }

    Get-Content -LiteralPath $secretsFile | ForEach-Object {
        $line = $_.Trim()
        if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith("#")) {
            return
        }

        $separatorIndex = $line.IndexOf("=")
        if ($separatorIndex -lt 1) {
            return
        }

        $name = $line.Substring(0, $separatorIndex).Trim()
        $value = $line.Substring($separatorIndex + 1).Trim()
        if ([string]::IsNullOrWhiteSpace($name)) {
            return
        }

        if (-not [string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable($name))) {
            return
        }

        [Environment]::SetEnvironmentVariable($name, $value)
    }
}

function Wait-ForDocker {
    Write-StructuredLog -Level "Information" -EventName "docker.check.started" -Message "Checking Docker Desktop"
    $dockerRunning = $false
    try {
        $null = docker info 2>&1
        if ($LASTEXITCODE -eq 0) { $dockerRunning = $true }
    } catch {}

    if ($dockerRunning) {
        Write-StructuredLog -Level "Information" -EventName "docker.check.ready" -Message "Docker Desktop is already running"
        return
    }

    Write-StructuredLog -Level "Warning" -EventName "docker.check.starting" -Message "Docker Desktop is not running. Starting it"
    Start-Process "C:\Program Files\Docker\Docker\Docker Desktop.exe"

    $timeout = 120
    $elapsed = 0
    while ($elapsed -lt $timeout) {
        Start-Sleep -Seconds 5
        $elapsed += 5
        try {
            $null = docker info 2>&1
            if ($LASTEXITCODE -eq 0) {
                Write-StructuredLog -Level "Information" -EventName "docker.check.ready" -Message "Docker Desktop is ready" -Properties @{
                    waitSeconds = $elapsed
                }
                return
            }
        } catch {}

        Write-StructuredLog -Level "Information" -EventName "docker.check.waiting" -Message "Waiting for Docker Desktop" -Properties @{
            waitSeconds = $elapsed
            timeoutSeconds = $timeout
        }
    }

    throw "Docker Desktop did not start within $timeout seconds."
}

function Wait-ForOpenSearch {
    Write-StructuredLog -Level "Information" -EventName "opensearch.check.started" -Message "Checking OpenSearch container"
    $containerStatus = docker ps --filter "name=crs-opensearch" --format "{{.Status}}" 2>&1
    if (-not $containerStatus -or $containerStatus -notlike "Up*") {
        Write-StructuredLog -Level "Warning" -EventName "opensearch.check.starting" -Message "OpenSearch container is not running. Starting it"
        docker compose -f "$repoRoot\docker-compose.yml" up -d opensearch
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to start OpenSearch container."
        }
    } else {
        Write-StructuredLog -Level "Information" -EventName "opensearch.check.ready" -Message "OpenSearch container is already running"
    }

    $timeout = 120
    $elapsed = 0
    while ($elapsed -lt $timeout) {
        try {
            $response = Invoke-RestMethod -Uri "http://localhost:9200/_cluster/health" -TimeoutSec 5 -ErrorAction SilentlyContinue
            if ($response.status -eq "green" -or $response.status -eq "yellow") {
                Write-StructuredLog -Level "Information" -EventName "opensearch.health.ready" -Message "OpenSearch is healthy" -Properties @{
                    clusterStatus = $response.status
                    waitSeconds = $elapsed
                }
                return
            }
        } catch {}

        Start-Sleep -Seconds 5
        $elapsed += 5
        Write-StructuredLog -Level "Information" -EventName "opensearch.health.waiting" -Message "Waiting for OpenSearch" -Properties @{
            waitSeconds = $elapsed
            timeoutSeconds = $timeout
        }
    }

    throw "OpenSearch did not become healthy within $timeout seconds."
}

$env:DOTNET_ENVIRONMENT = if ($env:DOTNET_ENVIRONMENT) { $env:DOTNET_ENVIRONMENT } else { "Production" }
$env:Observability__Environment = if ($env:Observability__Environment) { $env:Observability__Environment } else { "dev" }
$env:Observability__ExecutionEnvironment = $executionEnvironment

Import-LocalJobSecrets
$env:Observability__ServiceName = if ($env:Observability__ServiceName) { $env:Observability__ServiceName } else { $serviceName }
$env:OTEL_EXPORTER_OTLP_ENDPOINT = if ($env:OTEL_EXPORTER_OTLP_ENDPOINT) { $env:OTEL_EXPORTER_OTLP_ENDPOINT } else { "http://127.0.0.1:4317" }
$env:OTEL_EXPORTER_OTLP_PROTOCOL = if ($env:OTEL_EXPORTER_OTLP_PROTOCOL) { $env:OTEL_EXPORTER_OTLP_PROTOCOL } else { "grpc" }
$env:OTEL_METRICS_EXPORTER = if ($env:OTEL_METRICS_EXPORTER) { $env:OTEL_METRICS_EXPORTER } else { "none" }
$env:OTEL_LOGS_EXPORTER = if ($env:OTEL_LOGS_EXPORTER) { $env:OTEL_LOGS_EXPORTER } else { "none" }
$env:OTEL_PROPAGATORS = if ($env:OTEL_PROPAGATORS) { $env:OTEL_PROPAGATORS } else { "xray" }

Write-StructuredLog -Level "Information" -EventName "job.wrapper.started" -Message "Starting scheduled job wrapper" -Properties @{
    dotnetEnvironment = $env:DOTNET_ENVIRONMENT
    logPath = $jobLogPath
    otlpEndpoint = $env:OTEL_EXPORTER_OTLP_ENDPOINT
}
Write-MetricEvent -Name "job.host.heartbeat" -Value 1 -Unit "Count" -Operation "job.host" -Outcome "started" -Message "job host heartbeat emitted" -Properties @{
    logPath = $jobLogPath
}

try {
    if ($JobName -ne "x-ingestion") {
        Wait-ForDocker
        Wait-ForOpenSearch
    } else {
        Write-StructuredLog -Level "Information" -EventName "job.prerequisites.skipped" -Message "Skipping Docker and OpenSearch prerequisites for x-ingestion"
    }

    Write-StructuredLog -Level "Information" -EventName "job.process.started" -Message "Running Crs.Jobs" -Properties @{
        project = "src/Crs.Jobs"
        arguments = $JobName
    }

    Set-Location $repoRoot
    $exitCode = Invoke-JobProcess -Name $JobName -StreamToConsole:$Interactive

    $elapsedMs = [Math]::Round(((Get-Date) - $startedAt).TotalMilliseconds, 2)
    Write-StructuredLog -Level "Information" -EventName "job.wrapper.completed" -Message "Scheduled job wrapper completed" -Properties @{
        exitCode = $exitCode
        elapsedMilliseconds = $elapsedMs
    }

    if ($exitCode -eq 0) {
        Write-MetricEvent -Name "job.wrapper.success.count" -Value 1 -Unit "Count" -Operation "job.wrapper" -Outcome "success"
        Write-MetricEvent -Name "job.wrapper.duration" -Value $elapsedMs -Unit "Milliseconds" -Operation "job.wrapper" -Outcome "success"
    } else {
        Write-MetricEvent -Name "job.wrapper.failure.count" -Value 1 -Unit "Count" -Operation "job.wrapper" -Outcome "failed" -Properties @{
            exitCode = $exitCode
        }
        Write-MetricEvent -Name "job.wrapper.duration" -Value $elapsedMs -Unit "Milliseconds" -Operation "job.wrapper" -Outcome "failed" -Properties @{
            exitCode = $exitCode
        }
    }

    exit $exitCode
}
catch {
    $elapsedMs = [Math]::Round(((Get-Date) - $startedAt).TotalMilliseconds, 2)
    Write-StructuredLog -Level "Error" -EventName "job.wrapper.failed" -Message "Scheduled job wrapper failed" -Properties @{
        elapsedMilliseconds = $elapsedMs
        error = $_.Exception.Message
    }
    Write-MetricEvent -Name "job.wrapper.failure.count" -Value 1 -Unit "Count" -Operation "job.wrapper" -Outcome "failed" -Properties @{
        error = $_.Exception.Message
    }
    Write-MetricEvent -Name "job.wrapper.duration" -Value $elapsedMs -Unit "Milliseconds" -Operation "job.wrapper" -Outcome "failed" -Properties @{
        error = $_.Exception.Message
    }
    exit 1
}

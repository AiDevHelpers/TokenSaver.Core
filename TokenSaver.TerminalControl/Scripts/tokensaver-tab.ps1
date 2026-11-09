# Runs inside one Windows Terminal tab for a TokenSaver standard session.
# Reads "<id> <command>" lines from the command pipe, runs each command,
# streams stdout to the stdout pipe and error records to the stderr pipe,
# then writes "__TOKENSAVER_END__ <id> <exitcode>" to the stdout pipe.
# The reserved command "__TOKENSAVER_CLOSE__" ends the loop and the tab.
param(
    [Parameter(Mandatory)][string]$StdoutPipe,
    [Parameter(Mandatory)][string]$StderrPipe,
    [Parameter(Mandatory)][string]$CommandPipe
)

$ErrorActionPreference = 'Continue'
$endMarker = '__TOKENSAVER_END__'
$closeCommand = '__TOKENSAVER_CLOSE__'

# Output pipes are created by TokenSaver (server side); this tab connects as a client.
$outClient = [System.IO.Pipes.NamedPipeClientStream]::new('.', $StdoutPipe, [System.IO.Pipes.PipeDirection]::Out)
$errClient = [System.IO.Pipes.NamedPipeClientStream]::new('.', $StderrPipe, [System.IO.Pipes.PipeDirection]::Out)
$outClient.Connect(10000)
$errClient.Connect(10000)

$outW = [System.IO.StreamWriter]::new($outClient)
$outW.AutoFlush = $true
$outW.NewLine = "`n"
$errW = [System.IO.StreamWriter]::new($errClient)
$errW.AutoFlush = $true
$errW.NewLine = "`n"

# The command pipe is created here (server side); TokenSaver connects to it as a client.
$cmdServer = [System.IO.Pipes.NamedPipeServerStream]::new(
    $CommandPipe,
    [System.IO.Pipes.PipeDirection]::In,
    1,
    [System.IO.Pipes.PipeTransmissionMode]::Byte,
    [System.IO.Pipes.PipeOptions]::None)
$cmdServer.WaitForConnection()
$cmdReader = [System.IO.StreamReader]::new($cmdServer)

while ($null -ne ($line = $cmdReader.ReadLine())) {
    $space = $line.IndexOf(' ')
    if ($space -lt 1) { continue }

    $id = $line.Substring(0, $space)
    $command = $line.Substring($space + 1)

    if ($command -eq $closeCommand) {
        $outW.WriteLine("$endMarker $id 0")
        break
    }

    $global:LASTEXITCODE = 0
    $failed = $false
    try {
        & ([scriptblock]::Create($command)) 2>&1 | ForEach-Object {
            if ($_ -is [System.Management.Automation.ErrorRecord]) {
                $errW.WriteLine($_.ToString())
            }
            else {
                $outW.WriteLine([string]$_)
            }
        }
    }
    catch {
        $failed = $true
        $errW.WriteLine($_.ToString())
    }

    if ($failed) { $code = 1 }
    elseif ($global:LASTEXITCODE -is [int] -and $global:LASTEXITCODE -ne 0) { $code = $global:LASTEXITCODE }
    else { $code = 0 }

    $outW.WriteLine("$endMarker $id $code")
}

$cmdReader.Dispose()
$cmdServer.Dispose()
$outW.Dispose()
$errW.Dispose()

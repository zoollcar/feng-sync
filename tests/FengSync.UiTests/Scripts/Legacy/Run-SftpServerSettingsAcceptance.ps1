[CmdletBinding()]
param([string] $Configuration = 'Debug')
$ErrorActionPreference = 'Stop'

$workspace = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
$cleanup = Join-Path $workspace 'tests\Shared\TestProcessCleanup.ps1'; . $cleanup; Clear-FengSyncTestProcesses -Workspace $workspace
$testRunId = 'sftp-settings-' + [Guid]::NewGuid().ToString('N')
$app = Join-Path $workspace "src\FengSync\bin\$Configuration\net10.0-windows\FengSync.exe"
$modules = Join-Path $workspace '.fengsync-test\sftp-node\node_modules'
$node = (Get-Command node -ErrorAction Stop).Source
if (-not (Test-Path $app)) { throw "找不到应用：$app" }
if (-not (Test-Path (Join-Path $modules 'ssh2\package.json'))) {
  $fixture = Split-Path $modules -Parent; New-Item -ItemType Directory -Force -Path $fixture | Out-Null
  Copy-Item (Join-Path $workspace 'src\FengSync.Core\SftpServer\package.json') (Join-Path $fixture 'package.json') -Force
  Copy-Item (Join-Path $workspace 'src\FengSync.Core\SftpServer\package-lock.json') (Join-Path $fixture 'package-lock.json') -Force
  & npm ci --omit=dev --prefix $fixture
  if ($LASTEXITCODE -ne 0) { throw '无法准备固定 ssh2 GUI 测试依赖。' }
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
function Wait-Until([scriptblock]$Condition, [string]$Message, [int]$Seconds = 12) { $end = [DateTime]::UtcNow.AddSeconds($Seconds); while ([DateTime]::UtcNow -lt $end) { $v = & $Condition; if ($null -ne $v -and $v -ne $false) { return $v }; Start-Sleep -Milliseconds 120 }; throw $Message }
function Find-Id($root, [string]$id) { Wait-Until { $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id))) } "未找到UI元素 $id" }
function Find-Name($root, [string]$name) { Wait-Until { $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name))) } "未找到UI元素 $name" }
function Invoke-Element($element) { $p = $null; if ($element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$p)) { $p.Invoke(); return }; $e = $null; if ($element.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$e)) { $e.Expand(); return }; throw "元素无法调用：$($element.Current.Name)" }
function Select-Element($element) { $p = $null; if ($element.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$p)) { $p.Select(); return }; throw "元素无法选择：$($element.Current.Name)" }
function Can-Connect([int]$Port) { try { $c = [Net.Sockets.TcpClient]::new(); $c.Connect('127.0.0.1', $Port); $c.Dispose(); return $true } catch { return $false } }

$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $listener.Start(); $port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port; $listener.Stop()
$root = Join-Path $workspace '.fengsync-test\sftp-settings-acceptance'; Remove-Item -LiteralPath $root -Force -Recurse -ErrorAction SilentlyContinue; New-Item -ItemType Directory -Force -Path (Join-Path $root 'appdata\sftp'), (Join-Path $root 'share'), (Join-Path $root 'artifacts') | Out-Null
$salt = [Security.Cryptography.RandomNumberGenerator]::GetBytes(16); $password = 'settings-test-password'; $hash = [Security.Cryptography.Rfc2898DeriveBytes]::Pbkdf2($password, $salt, 210000, [Security.Cryptography.HashAlgorithmName]::SHA256, 32)
$settings = [ordered]@{ SchemaVersion = 2; Enabled = $false; StartWithApplication = $false; ListenAddress = '127.0.0.1'; Port = $port; MaxConnections = 2; IdleTimeout = $null; Accounts = @([ordered]@{ UserName = 'settings'; Enabled = $true; PasswordSalt = [Convert]::ToBase64String($salt); PasswordHash = [Convert]::ToBase64String($hash); PasswordIterations = 210000; PublicKeys = $null }); Shares = @([ordered]@{ VirtualName = 'docs'; PhysicalPath = (Join-Path $root 'share'); Permission = 1 }); NodeExecutablePath = $node; NodeModulePath = $modules }
$settings | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $root 'appdata\sftp\sftp-server.json') -Encoding utf8

$process = $null
try {
  $start = [Diagnostics.ProcessStartInfo]::new($app); $start.UseShellExecute = $false; $start.Arguments = "--fengsync-test-run-id $testRunId"; $start.EnvironmentVariables['FENGSYNC_DATA_DIR'] = (Join-Path $root 'appdata'); $process = [Diagnostics.Process]::Start($start)
  $main = Wait-Until { if ($process.MainWindowHandle -ne 0) { [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle) } } '主窗口未出现'
  Invoke-Element (Find-Name $main '工具'); Invoke-Element (Find-Name ([System.Windows.Automation.AutomationElement]::RootElement) '选项…')
  $settingsWindow = Wait-Until { [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, '程序设置'))) } '程序设置窗口未打开'
  Select-Element (Find-Name $settingsWindow '集成')
  Invoke-Element (Find-Id $settingsWindow 'SftpServerSettingsButton')
  $dialog = Wait-Until { [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'SFTP 服务器设置'))) } 'SFTP设置窗口未打开'
  Invoke-Element (Find-Id $dialog 'StartSftpServer')
  Wait-Until { Can-Connect $port } 'SFTP设置窗口启动后端口未监听'
  Invoke-Element (Find-Id $dialog 'StopSftpServer')
  Wait-Until { -not (Can-Connect $port) } 'SFTP设置窗口停止后端口仍被监听'
  $bmp = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds; Add-Type -AssemblyName System.Drawing; $image = New-Object Drawing.Bitmap $bmp.Width,$bmp.Height; $graphics = [Drawing.Graphics]::FromImage($image); $graphics.CopyFromScreen($bmp.Location, [Drawing.Point]::Empty, $bmp.Size); $image.Save((Join-Path $root 'artifacts\sftp-settings-start-stop.png')); $graphics.Dispose(); $image.Dispose()
  [pscustomobject]@{ Result = 'Passed'; Port = $port; Artifact = (Join-Path $root 'artifacts\sftp-settings-start-stop.png') } | ConvertTo-Json
}
finally { if ($process) { if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }; $process.Dispose() } }

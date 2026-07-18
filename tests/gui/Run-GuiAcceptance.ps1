[CmdletBinding()]
param(
    [string] $Configuration = 'Debug',
    [switch] $SkipBuild,
    [switch] $IncludeSftp,
    [string] $SftpHost = '127.0.0.1',
    [int] $SftpPort = 2222,
    [string] $SftpUser = 'fengsync',
    [string] $SftpPassword = 'fengsync-test',
    [string] $SftpRemoteName = 'fengsync_gui',
    [string] $SftpShareName = 'docs'
)

$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$testRoot = Join-Path $workspace '.fengsync-test\gui-acceptance'
$appData = Join-Path $testRoot 'appdata'
$left = Join-Path $testRoot 'left'
$right = Join-Path $testRoot 'right'
$artifacts = Join-Path $testRoot 'artifacts'
$app = Join-Path $workspace "src\FengSync\bin\$Configuration\net10.0-windows\FengSync.exe"

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing

function Assert-That([bool] $condition, [string] $message) { if (-not $condition) { throw $message } }
function Wait-Until([scriptblock] $condition, [int] $timeoutSeconds, [string] $message) {
    $deadline = [DateTime]::UtcNow.AddSeconds($timeoutSeconds)
    do { $value = & $condition; if ($value) { return $value }; Start-Sleep -Milliseconds 150 } while ([DateTime]::UtcNow -lt $deadline)
    throw $message
}
function Find-ById($root, [string] $id, [int] $timeoutSeconds = 12) {
    return Wait-Until -condition { $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id))) } -timeoutSeconds $timeoutSeconds -message "未找到 UI 元素：$id"
}
function Find-ByName($root, [string] $name, [int] $timeoutSeconds = 12) {
    return Wait-Until -condition { $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name))) } -timeoutSeconds $timeoutSeconds -message "未找到 UI 元素：$name"
}
function Set-Value($element, [string] $value) {
    $pattern = $null
    Assert-That ($element.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref] $pattern)) '控件不支持 ValuePattern。'
    $pattern.SetValue($value)
}
function Invoke-Ui($element) {
    $pattern = $null
    Assert-That ($element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref] $pattern)) '控件不支持 InvokePattern。'
    $pattern.Invoke()
}
function Select-Ui($element) {
    $pattern = $null
    Assert-That ($element.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref] $pattern)) '控件不支持 SelectionItemPattern。'
    $pattern.Select()
}
function Select-ComboItem($combo, [string] $name) {
    $expand = $null
    Assert-That ($combo.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref] $expand)) '同步模式控件不支持展开。'
    $expand.Expand()
    $item = Find-ByName $combo $name
    Select-Ui $item
}
function Capture-Window([System.Diagnostics.Process] $process, [string] $name) {
    $bounds = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle).Current.BoundingRectangle
    if ($bounds.Width -le 0 -or $bounds.Height -le 0) { return }
    $image = New-Object System.Drawing.Bitmap([int]$bounds.Width, [int]$bounds.Height)
    $graphics = [System.Drawing.Graphics]::FromImage($image)
    try { $graphics.CopyFromScreen([int]$bounds.X, [int]$bounds.Y, 0, 0, $image.Size); $image.Save((Join-Path $artifacts $name), [System.Drawing.Imaging.ImageFormat]::Png) }
    finally { $graphics.Dispose(); $image.Dispose() }
}

if (Test-Path $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path $left, $right, $artifacts, $appData | Out-Null
Set-Content -LiteralPath (Join-Path $left 'from-ui.txt') -Value 'created by GUI acceptance test' -NoNewline

if (-not $SkipBuild) { & dotnet build (Join-Path $workspace 'FengSync.sln') --nologo; Assert-That ($LASTEXITCODE -eq 0) '构建失败。' }
Assert-That (Test-Path $app) "未找到应用程序：$app"

$previousFengSyncData = $env:FENGSYNC_DATA_DIR
$env:FENGSYNC_DATA_DIR = $appData
$sftpEndpoint = "sftp://$SftpRemoteName/$SftpShareName"
if ($IncludeSftp) {
    $rclone = Join-Path (Split-Path $app) 'Assets\rclone\rclone.exe'
    $rcloneConfig = Join-Path $appData 'rclone\rclone.conf'
    New-Item -ItemType Directory -Force -Path (Split-Path $rcloneConfig) | Out-Null
    & $rclone config create $SftpRemoteName sftp host $SftpHost user $SftpUser port "$SftpPort" pass $SftpPassword --config $rcloneConfig
    Assert-That ($LASTEXITCODE -eq 0) '无法创建 GUI 验收专用 rclone SFTP 端点。'
}
# Explicitly inject the isolated data root: Start-Process inheritance differs between hosts/CI agents.
$process = Start-Process -FilePath $app -Environment @{ FENGSYNC_DATA_DIR = $appData } -PassThru
try {
    Wait-Until -condition { $process.Refresh() | Out-Null; return $process.MainWindowHandle -ne 0 } -timeoutSeconds 20 -message 'Feng Sync 未创建主窗口。'
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    Assert-That ($root.Current.Name -eq 'Feng Sync') '主窗口标题不正确。'
    Capture-Window $process '01-main.png'

    # Profile 管理：新建档案、打开编辑器、取消。该进程的 LocalAppData 已隔离。
    Invoke-Ui (Find-ById $root 'NewProfileButton')
    Wait-Until -condition { (Find-ById $root 'ProfileList').FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition).Count -ge 2 } -timeoutSeconds 10 -message '新建 Profile 没有出现在列表中。'
    $profileItems = (Find-ById $root 'ProfileList').FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
    Select-Ui $profileItems[$profileItems.Count - 1]
    Start-Sleep -Milliseconds 250
    Invoke-Ui (Find-ById $root 'EditProfileButton')
    Start-Sleep -Milliseconds 500
    Capture-Window $process 'profile-editor-attempt.png'
    $editor = Wait-Until -condition { [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Profile 设置'))) } -timeoutSeconds 10 -message 'Profile 编辑器未打开。'
    Assert-That ((Find-ById $editor 'NameBox').Current.IsEnabled) 'Profile 名称不可编辑。'
    Invoke-Ui (Find-ByName $editor '取消')

    # 主流程：从 UI 填写两个本地端点，比较并同步。
    Set-Value (Find-ById $root 'LeftPath') $left
    Set-Value (Find-ById $root 'RightPath') $right
    Invoke-Ui (Find-ById $root 'CompareButton')
    $sync = Find-ById $root 'SyncButton'
    Wait-Until -condition { $sync.Current.IsEnabled } -timeoutSeconds 20 -message '比较完成后同步按钮没有启用。'
    Capture-Window $process '02-compared.png'
    Invoke-Ui $sync
    Wait-Until -condition { Test-Path (Join-Path $right 'from-ui.txt') } -timeoutSeconds 30 -message '通过 UI 执行的本地同步未写入目标文件。'
    Capture-Window $process '03-synchronized.png'

    if ($IncludeSftp) {
        # SFTP 场景仍通过可见主界面设置端点；端点由本脚本创建的隔离 rclone 配置提供。
        Select-ComboItem (Find-ById $root 'SyncModeBox') '更新 →'
        Set-Value (Find-ById $root 'LeftPath') $left
        Set-Value (Find-ById $root 'RightPath') $sftpEndpoint
        Invoke-Ui (Find-ById $root 'CompareButton')
        try { Wait-Until -condition { (Find-ById $root 'SyncButton').Current.IsEnabled } -timeoutSeconds 20 -message 'SFTP 比较后同步按钮没有启用。' }
        catch { Capture-Window $process '04-sftp-compare-failed.png'; throw "SFTP 比较未生成同步计划：$((Find-ById $root 'Status').Current.Name)" }
        Invoke-Ui (Find-ById $root 'SyncButton')
        Capture-Window $process '04-sftp-synchronized.png'
    }

    [pscustomobject]@{ Result = 'Passed'; Root = $testRoot; Screenshots = (Get-ChildItem $artifacts -Filter '*.png').FullName } | ConvertTo-Json -Depth 3
}
finally {
    if ($process -and -not $process.HasExited) { $process.CloseMainWindow() | Out-Null; Start-Sleep -Milliseconds 500; if (-not $process.HasExited) { $process.Kill($true) } }
    $env:FENGSYNC_DATA_DIR = $previousFengSyncData
}

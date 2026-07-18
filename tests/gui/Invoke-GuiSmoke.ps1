[CmdletBinding()]
param(
    [string]$AppPath = (Join-Path $PSScriptRoot '..\..\src\FengSync\bin\Debug\net10.0-windows\FengSync.exe'),
    # A preconfigured URI, e.g. sftp://fengsync_gui/ . The script exercises the actual GUI sync path.
    [string]$SftpUri,
    [switch]$RequireSftp,
    [string]$ArtifactsDirectory = (Join-Path $PSScriptRoot '..\..\artifacts\gui-smoke')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
$AppPath = [IO.Path]::GetFullPath($AppPath)
if (-not (Test-Path -LiteralPath $AppPath)) { throw "应用程序不存在：$AppPath。请先运行 dotnet build FengSync.sln。" }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$runRoot = Join-Path ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\.fengsync-test'))) "gui-$stamp"
$left = Join-Path $runRoot 'left'; $right = Join-Path $runRoot 'right'; $remoteSource = Join-Path $runRoot 'remote-source'
New-Item -ItemType Directory -Force -Path $left, $right, $remoteSource, $ArtifactsDirectory | Out-Null
[IO.File]::WriteAllText((Join-Path $left 'local-proof.txt'), "local-$stamp")
[IO.File]::WriteAllText((Join-Path $remoteSource 'sftp-proof.txt'), "sftp-$stamp")

function Wait-Element([System.Windows.Automation.AutomationElement]$Scope, [System.Windows.Automation.Condition]$Condition, [int]$Seconds = 15) {
    $until = [DateTime]::UtcNow.AddSeconds($Seconds)
    do { $item = $Scope.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $Condition); if ($null -ne $item) { return $item }; Start-Sleep -Milliseconds 150 } while ([DateTime]::UtcNow -lt $until)
    throw "UI 元素未出现：$Condition"
}
function Find-Id($Scope, [string]$Id, [int]$Seconds = 15) { Wait-Element $Scope ([System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)) $Seconds }
function Find-Name($Scope, [string]$Name, [int]$Seconds = 15) { Wait-Element $Scope ([System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $Name)) $Seconds }
function Set-Text($Element, [string]$Text) {
    $pattern = $Element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    ([System.Windows.Automation.ValuePattern]$pattern).SetValue($Text)
}
function Click($Element) { ([System.Windows.Automation.InvokePattern]$Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke() }
function Capture([string]$Name) {
    $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $image = [System.Drawing.Bitmap]::new($bounds.Width, $bounds.Height)
    $graphics = [System.Drawing.Graphics]::FromImage($image)
    try { $graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size); $image.Save((Join-Path $ArtifactsDirectory "$stamp-$Name.png"), [System.Drawing.Imaging.ImageFormat]::Png) }
    finally { $graphics.Dispose(); $image.Dispose() }
}
function Assert-Eventually([scriptblock]$Condition, [string]$Message, [int]$Seconds = 25) {
    $until = [DateTime]::UtcNow.AddSeconds($Seconds)
    do { if (& $Condition) { return }; Start-Sleep -Milliseconds 200 } while ([DateTime]::UtcNow -lt $until)
    throw $Message
}

$process = Start-Process -FilePath $AppPath -PassThru
try {
    $main = Wait-Element ([System.Windows.Automation.AutomationElement]::RootElement) ([System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)) 20
    Capture 'main'

    # Profile management: create, edit and save a detached profile through the visible editor.
    Click (Find-Name $main '新建')
    Click (Find-Name $main '编辑 Profile…')
    $editor = Find-Name ([System.Windows.Automation.AutomationElement]::RootElement) 'Profile 设置'
    Set-Text (Find-Id $editor 'NameBox') "GUI local $stamp"
    Set-Text (Find-Id $editor 'LeftPathBox') $left
    Set-Text (Find-Id $editor 'RightPathBox') $right
    Click (Find-Name $editor '保存')
    Assert-Eventually { (Find-Id $main 'LeftPath').Current.Name -eq $left } '保存 Profile 后左端点没有回填。'
    Capture 'profile-saved'

    # Local GUI comparison and transfer. This validates planner, selection and progress-window lifecycle.
    Click (Find-Id $main 'CompareButton')
    Assert-Eventually { (Find-Id $main 'SyncButton').Current.IsEnabled } '本地比较没有产生可执行同步计划。'
    Capture 'local-compared'
    Click (Find-Id $main 'SyncButton')
    Assert-Eventually { Test-Path -LiteralPath (Join-Path $right 'local-proof.txt') } '本地 GUI 同步未将文件写入右侧。'
    Assert-Eventually { (Find-Id $main 'Status').Current.Name -match '同步完成' } '本地 GUI 同步没有完成状态。'
    Capture 'local-complete'

    if ($SftpUri) {
        # SftpUri must refer to the deterministic SFTP fixture configured before this script runs.
        Set-Text (Find-Id $main 'LeftPath') $remoteSource
        Set-Text (Find-Id $main 'RightPath') $SftpUri
        Click (Find-Id $main 'CompareButton')
        Assert-Eventually { (Find-Id $main 'SyncButton').Current.IsEnabled } 'SFTP 比较没有产生可执行同步计划。' 40
        Click (Find-Id $main 'SyncButton')
        Assert-Eventually { (Find-Id $main 'Status').Current.Name -match '同步完成' } 'SFTP GUI 同步没有完成状态。' 60
        Capture 'sftp-complete'
    } elseif ($RequireSftp) { throw 'RequireSftp 已指定，但未提供 -SftpUri。' }

    [pscustomobject]@{ Result = 'Passed'; LocalRoot = $runRoot; SftpExercised = [bool]$SftpUri; Artifacts = [IO.Path]::GetFullPath($ArtifactsDirectory) } | ConvertTo-Json -Compress
}
finally {
    if ($process -and -not $process.HasExited) { $process.CloseMainWindow() | Out-Null; Start-Sleep -Milliseconds 500; if (-not $process.HasExited) { $process.Kill() } }
}

[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('local', 'modes', 'selection', 'sftp-to-local', 'sftp-ui', 'profile', 'profile-filter', 'delete-threshold', 'settings', 'history', 'schedule', 'gdrive', 'gdrive-volume')][string]$Scenario,
    [Parameter(Mandatory)][string]$AppPath,
    [Parameter(Mandatory)][string]$Workspace
)

# Every scenario uses a new data root. On failure it is retained with its logs and
# screenshots; remote Google Drive cleanup is constrained to a generated child below
# the fixed test/FengSync-Automated-Tests test root.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
$AppPath = [IO.Path]::GetFullPath($AppPath); $Workspace = [IO.Path]::GetFullPath($Workspace)
if (-not (Test-Path -LiteralPath $AppPath)) { throw "Application not found: $AppPath" }
$stamp = "ui-$Scenario-" + [Guid]::NewGuid().ToString('N')
$root = Join-Path $Workspace ('.fengsync-test\ui\' + $stamp)
$appData = Join-Path $root 'appdata'; $artifacts = Join-Path $root 'artifacts'
New-Item -ItemType Directory -Force -Path $root, $appData, $artifacts | Out-Null
$scenarioTimer = [Diagnostics.Stopwatch]::StartNew()

function Wait-Until([scriptblock]$Condition, [string]$Message, [int]$Seconds = 30) {
  $end = [DateTime]::UtcNow.AddSeconds($Seconds)
  do { $value = & $Condition; if ($null -ne $value -and $value -ne $false) { return $value }; Start-Sleep -Milliseconds 150 } while ([DateTime]::UtcNow -lt $end)
  throw $Message
}
function Find-Id($root, [string]$id, [int]$seconds = 20) { Wait-Until { try { $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)) } catch { $null } } "Missing UI element: $id" $seconds }
function Find-Name($root, [string]$name, [int]$seconds = 20) { Wait-Until { try { $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $name)) } catch { $null } } "Missing UI element: $name" $seconds }
function Click($element) { $p = $null; if (-not $element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$p)) { throw "Element cannot be invoked: $($element.Current.Name)" }; $p.Invoke() }
function Open-Menu($element) { $p = $null; if ($element.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$p)) { $p.Expand(); return }; Click $element }
function Set-Text($element, [string]$value) { $p = $null; if (-not $element.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$p)) { throw "Element has no ValuePattern: $($element.Current.Name)" }; $p.SetValue($value) }
function Set-Password($element, [string]$value) { $element.SetFocus(); [Windows.Forms.SendKeys]::SendWait($value) }
function Select-Ui($element) { $p = $null; if (-not $element.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$p)) { throw "Element cannot be selected: $($element.Current.Name)" }; $p.Select() }
function Toggle-Ui($element) { $p = $null; if (-not $element.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$p)) { throw "Element cannot be toggled: $($element.Current.Name)" }; $p.Toggle() }
function Get-Text($element) { $p = $null; if (-not $element.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$p)) { throw "Element has no ValuePattern: $($element.Current.Name)" }; return $p.Current.Value }
function Find-WindowLike([string]$titleFragment, [int]$seconds = 20) { Wait-Until { try { $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)); foreach ($window in $windows) { if ($window.Current.Name -like "*$titleFragment*") { return $window } } } catch { $null } } "Missing window containing: $titleFragment" $seconds }
function Select-Mode($main, [string]$name) { $combo = Find-Id $main 'SyncModeBox'; $p = $null; if (-not $combo.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$p)) { throw 'Sync mode cannot expand.' }; $p.Expand(); Select-Ui (Find-Name $combo $name) }
function Approve-ConfirmationIfPresent {
  $confirm = try { [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, '确认同步操作')) } catch { $null }
  if ($confirm) { Click (Find-Name $confirm '确认同步' 2); return $true }
  return $false
}
function Wait-Sync { param($main, [string]$expectedFile, [int]$comparisonSeconds = 120, [int]$transferSeconds = 120)
  try { Wait-Until { (Find-Id $main 'SyncButton').Current.IsEnabled } 'Comparison did not produce an executable plan' $comparisonSeconds }
  catch { $status = try { (Find-Id $main 'Status').Current.Name } catch { 'unavailable' }; throw "Comparison did not produce an executable plan. UI status: $status" }
  Click (Find-Id $main 'SyncButton')
  # The handler re-scans remote endpoints before showing this dialog, so it can
  # appear asynchronously rather than immediately after the button invocation.
  try { Wait-Until { Approve-ConfirmationIfPresent | Out-Null; Test-Path -LiteralPath $expectedFile } "Expected synchronized file was not created: $expectedFile" $transferSeconds }
  catch { $status = try { (Find-Id $main 'Status').Current.Name } catch { 'unavailable' }; throw "Expected synchronized file was not created: $expectedFile. UI status: $status" }
  Wait-Until { (Find-Id $main 'Status').Current.Name -match '同步完成' } 'UI did not report synchronization completion' $transferSeconds
}
function Compare-Ui { param($main, [string]$left, [string]$right); Set-Text (Find-Id $main 'LeftPath') $left; Set-Text (Find-Id $main 'RightPath') $right; Click (Find-Id $main 'CompareButton') }
function New-SmallFiles { param([string]$directory, [int]$count)
  New-Item -ItemType Directory -Force -Path $directory | Out-Null
  for ($i = 1; $i -le $count; $i++) { [IO.File]::WriteAllText((Join-Path $directory ('batch-{0:D3}.txt' -f $i)), "small performance fixture $i") }
}
function Invoke-MeasuredSync { param($main, [string]$left, [string]$right, [string]$expectedFile, [int]$comparisonSeconds = 180, [int]$transferSeconds = 600)
  $comparisonTimer = [Diagnostics.Stopwatch]::StartNew()
  Compare-Ui $main $left $right
  try { Wait-Until { (Find-Id $main 'SyncButton').Current.IsEnabled } 'Comparison did not produce an executable plan' $comparisonSeconds }
  catch { $status = try { (Find-Id $main 'Status').Current.Name } catch { 'unavailable' }; throw "Comparison did not produce an executable plan. UI status: $status" }
  $comparisonTimer.Stop()
  $syncTimer = [Diagnostics.Stopwatch]::StartNew()
  Click (Find-Id $main 'SyncButton')
  try { Wait-Until { Approve-ConfirmationIfPresent | Out-Null; Test-Path -LiteralPath $expectedFile } "Expected synchronized file was not created: $expectedFile" $transferSeconds }
  catch { $status = try { (Find-Id $main 'Status').Current.Name } catch { 'unavailable' }; throw "Expected synchronized file was not created: $expectedFile. UI status: $status" }
  Wait-Until { (Find-Id $main 'Status').Current.Name -match '同步完成' } 'UI did not report synchronization completion' $transferSeconds
  $syncTimer.Stop()
  [pscustomobject]@{ CompareMilliseconds = $comparisonTimer.ElapsedMilliseconds; SyncMilliseconds = $syncTimer.ElapsedMilliseconds }
}
function Assert-GoogleDriveBatch { param([string]$remotePath, [int]$count)
  Wait-Until {
    $listing = & $rclone lsf $remotePath --recursive --config $config 2>$null
    $LASTEXITCODE -eq 0 -and @($listing | Where-Object { $_ -match '(^|/)batch-\d{3}\.txt$' }).Count -eq $count
  } "Google Drive did not contain all $count batch fixture files: $remotePath" 180
}
function Start-App {
  $start = [Diagnostics.ProcessStartInfo]::new($AppPath); $start.UseShellExecute = $false; $start.EnvironmentVariables['FENGSYNC_DATA_DIR'] = $appData
  $p = [Diagnostics.Process]::Start($start)
  $main = Wait-Until { if ($p.MainWindowHandle -ne 0) { [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle) } } 'Main window did not appear'
  return @($p, $main)
}
function Stop-App { param($p); if ($p -and -not $p.HasExited) { $p.Kill($true); $p.WaitForExit() }; if ($p) { $p.Dispose() } }
function Assert-File { param([string]$path, [string]$content); if (-not (Test-Path -LiteralPath $path)) { throw "Expected file not found: $path" }; if ([IO.File]::ReadAllText($path) -ne $content) { throw "Unexpected content in $path" } }
function Capture-Window { param($p, [string]$name)
  try {
    if (-not $p -or $p.MainWindowHandle -eq 0) { return }
    $bounds = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle).Current.BoundingRectangle
    if ($bounds.Width -le 0 -or $bounds.Height -le 0) { return }
    $image = [Drawing.Bitmap]::new([int]$bounds.Width, [int]$bounds.Height); $graphics = [Drawing.Graphics]::FromImage($image)
    try { $graphics.CopyFromScreen([int]$bounds.X, [int]$bounds.Y, 0, 0, $image.Size); $image.Save((Join-Path $artifacts $name), [Drawing.Imaging.ImageFormat]::Png) }
    finally { $graphics.Dispose(); $image.Dispose() }
  } catch { Write-Verbose "Could not capture UI screenshot: $_" }
}

$process = $null; $server = $null; $remoteCleanup = $null; $sftpUri = $null; $driveUri = $null; $driveChild = $null; $scheduledTask = $null; $passed = $false
try {
  if ($Scenario -in @('sftp-to-local', 'sftp-ui')) {
    $fixture = Join-Path $root 'sftp'; $share = Join-Path $fixture 'share'; $modules = Join-Path $Workspace '.fengsync-test\sftp-node\node_modules'
    New-Item -ItemType Directory -Force -Path $share | Out-Null
    if (-not (Test-Path (Join-Path $modules 'ssh2\package.json'))) {
      $moduleRoot = Split-Path $modules -Parent; New-Item -ItemType Directory -Force -Path $moduleRoot | Out-Null
      Copy-Item (Join-Path $Workspace 'src\FengSync.Core\SftpServer\package.json') (Join-Path $moduleRoot 'package.json') -Force
      Copy-Item (Join-Path $Workspace 'src\FengSync.Core\SftpServer\package-lock.json') (Join-Path $moduleRoot 'package-lock.json') -Force
      & npm ci --omit=dev --prefix $moduleRoot; if ($LASTEXITCODE -ne 0) { throw 'Unable to install pinned SFTP fixture dependency.' }
    }
    [IO.File]::WriteAllText((Join-Path $share 'remote-proof.txt'), 'from-sftp')
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $listener.Start(); $port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port; $listener.Stop()
    $password = 'ui-sftp-password'; $salt = [Security.Cryptography.RandomNumberGenerator]::GetBytes(16); $hash = [Security.Cryptography.Rfc2898DeriveBytes]::Pbkdf2($password, $salt, 210000, [Security.Cryptography.HashAlgorithmName]::SHA256, 32)
    $options = @{ Enabled=$true; ListenAddress='127.0.0.1'; Port=$port; MaxConnections=2; Accounts=@(@{UserName='ui';Enabled=$true;PasswordSalt=[Convert]::ToBase64String($salt);PasswordHash=[Convert]::ToBase64String($hash);PasswordIterations=210000;PublicKeys=@()}); Shares=@(@{VirtualName='docs';PhysicalPath=$share;Permission='ReadWrite'}) }
    $payload = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes((@{ Options=$options; HostKeyPath=(Join-Path $Workspace '.fengsync-test\real-sftp-host.pem') } | ConvertTo-Json -Depth 8 -Compress)))
    $start = [Diagnostics.ProcessStartInfo]::new((Get-Command node -ErrorAction Stop).Source); $start.Arguments = '"' + (Join-Path $Workspace 'src\FengSync.Core\SftpServer\node-sftp-host.cjs') + '"'; $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.EnvironmentVariables['FENGSYNC_SFTP_CONFIG']=$payload; $start.EnvironmentVariables['NODE_PATH']=$modules
    $server = [Diagnostics.Process]::Start($start); Wait-Until { try { $tcp=[Net.Sockets.TcpClient]::new();$tcp.Connect('127.0.0.1',$port);$tcp.Dispose();$true } catch {$false} } 'SFTP fixture did not start'
    if ($Scenario -eq 'sftp-to-local') { $rclone = Join-Path (Split-Path $AppPath) 'Assets\rclone\rclone.exe'; $config = Join-Path $appData 'rclone\rclone.conf'; New-Item -ItemType Directory -Force -Path (Split-Path $config) | Out-Null
      & $rclone config create ui_sftp sftp host 127.0.0.1 user ui port "$port" pass $password --config $config; if ($LASTEXITCODE -ne 0) { throw 'Could not configure isolated SFTP remote.' }; $sftpUri='sftp://ui_sftp/docs' }
  }
  if ($Scenario -in @('gdrive', 'gdrive-volume')) {
    # Discover the currently configured Feng Sync Google Drive credential.  The test
    # never guesses a remote name and only creates a random child below this fixed,
    # application-owned test root.
    $sourceConfig = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'FengSync\rclone\rclone.conf'
    if (-not (Test-Path -LiteralPath $sourceConfig)) { Write-Output 'SKIPPED: no Feng Sync rclone configuration was found.'; exit 77 }
    $rclone = Join-Path (Split-Path $AppPath) 'Assets\rclone\rclone.exe'
    $dump = & $rclone config dump --config $sourceConfig
    if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the current Feng Sync rclone configuration.' }
    $accounts = $dump | ConvertFrom-Json
    $driveRemote = @($accounts.psobject.Properties | Where-Object { $_.Value.type -eq 'drive' } | Select-Object -First 1).Name
    if ([string]::IsNullOrWhiteSpace($driveRemote)) { Write-Output 'SKIPPED: no Google Drive credential is configured in Feng Sync.'; exit 77 }
    # The configured credential is allowed to be exercised only below its test
    # directory. The generated child prevents this test from touching user files.
    $driveUri = "gdrive://$driveRemote/test/FengSync-Automated-Tests"
    # The durable test root is intentionally retained. Only the generated child
    # beneath it is test data and will be purged in finally.
    & $rclone mkdir "${driveRemote}:test" --config $sourceConfig
    if ($LASTEXITCODE -ne 0) { throw "Google Drive credential '$driveRemote' cannot create or access its required text test root." }
    $config = Join-Path $appData 'rclone\rclone.conf'; New-Item -ItemType Directory -Force -Path (Split-Path $config) | Out-Null; Copy-Item -LiteralPath $sourceConfig -Destination $config -Force
    $driveChild = 'fengsync-ui-' + [Guid]::NewGuid().ToString('N'); $driveUri = $driveUri.TrimEnd('/') + '/' + $driveChild
    # Feng Sync scans both endpoint roots before planning. Google Drive does not
    # reliably materialize an empty directory, so place one disposable marker in
    # this generated child; the visible UI flow still performs the proof upload.
    & $rclone mkdir "${driveRemote}:test/FengSync-Automated-Tests/$driveChild" --config $sourceConfig
    if ($LASTEXITCODE -ne 0) { throw "Could not create Google Drive test child: $driveUri" }
    $anchor = Join-Path $root '.fengsync-fixture-anchor'; [IO.File]::WriteAllText($anchor, 'fixture')
    & $rclone copyto $anchor "${driveRemote}:test/FengSync-Automated-Tests/$driveChild/.fengsync-fixture-anchor" --config $sourceConfig
    if ($LASTEXITCODE -ne 0) { throw "Could not materialize Google Drive test child: $driveUri" }
    $remoteCleanup = $driveUri
  }
  $launch = Start-App; $process = $launch[0]; $main = $launch[1]
  Capture-Window $process '01-main.png'
  switch ($Scenario) {
    'local' {
      $left = Join-Path $root 'left'; $right = Join-Path $root 'right'; New-Item -ItemType Directory -Force -Path @($left, $right) | Out-Null
      [IO.File]::WriteAllText((Join-Path $left 'initial.txt'), 'left-initial')
      Compare-Ui $main $left $right; Wait-Sync $main (Join-Path $right 'initial.txt'); Assert-File (Join-Path $right 'initial.txt') 'left-initial'
      # Establish a true two-way conflict, select its row, choose the right-to-left
      # direction in the visible UI, then prove the left file is the right content.
      [IO.File]::WriteAllText((Join-Path $left 'initial.txt'), 'left-change'); [IO.File]::WriteAllText((Join-Path $right 'initial.txt'), 'right-change')
      Start-Sleep -Milliseconds 2200; Compare-Ui $main $left $right
      $grid = Find-Id $main 'Comparison'; $rows = $grid.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
      $row = Wait-Until { $items = $grid.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::DataItem)); if ($items.Count -gt 0) { $items[0] } } 'No comparison row was shown'
      Select-Ui $row
      Click (Find-Id $main 'KeepRightButton'); Wait-Sync $main (Join-Path $left 'initial.txt'); Assert-File (Join-Path $left 'initial.txt') 'right-change'
    }
    'modes' {
      $left = Join-Path $root 'left'; $right = Join-Path $root 'right'; New-Item -ItemType Directory -Force -Path @($left, $right) | Out-Null
      [IO.File]::WriteAllText((Join-Path $left 'from-left.txt'), 'left'); [IO.File]::WriteAllText((Join-Path $right 'right-only.txt'), 'preserve')
      Select-Mode $main '更新 →'; Compare-Ui $main $left $right; Wait-Sync $main (Join-Path $right 'from-left.txt'); Assert-File (Join-Path $right 'right-only.txt') 'preserve'
      [IO.File]::WriteAllText((Join-Path $right 'remove-in-mirror.txt'), 'delete')
      Select-Mode $main '镜像 →'; Compare-Ui $main $left $right
      Wait-Until { (Find-Id $main 'SyncButton').Current.IsEnabled } 'Mirror did not create a plan'; Click (Find-Id $main 'SyncButton')
      $confirm = Wait-Until { [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, '确认同步操作')) } 'Mirror deletion did not ask for confirmation'; Click (Find-Name $confirm '确认同步')
      Wait-Until { -not (Test-Path -LiteralPath (Join-Path $right 'remove-in-mirror.txt')) -and -not (Test-Path -LiteralPath (Join-Path $right 'right-only.txt')) } 'Mirror did not delete every right-only file' 60
      Assert-File (Join-Path $right 'from-left.txt') 'left'
    }
    'selection' {
      $left = Join-Path $root 'left'; $right = Join-Path $root 'right'; New-Item -ItemType Directory -Force -Path @($left, $right) | Out-Null
      $excluded = Join-Path $left 'not-selected.txt'; [IO.File]::WriteAllText($excluded, 'must-stay-local')
      Compare-Ui $main $left $right
      $grid = Find-Id $main 'Comparison'
      $row = Wait-Until { $items = $grid.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::DataItem)); if ($items.Count -gt 0) { $items[0] } } 'Comparison row was not exposed.'
      $box = Wait-Until { $items = $row.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::CheckBox)); if ($items.Count -gt 0) { $items[0] } } 'Comparison selection checkbox was not exposed.'
      Toggle-Ui $box
      Wait-Until { -not (Find-Id $main 'SyncButton').Current.IsEnabled } 'Deselecting the only planned file did not disable sync.'
      if (Test-Path -LiteralPath (Join-Path $right 'not-selected.txt')) { throw 'Deselected file was unexpectedly synchronized.' }
    }
    'sftp-to-local' {
      $local = Join-Path $root 'download'; New-Item -ItemType Directory -Force -Path $local | Out-Null
      Select-Mode $main '更新 →'; Compare-Ui $main $sftpUri $local; Wait-Sync $main (Join-Path $local 'remote-proof.txt'); Assert-File (Join-Path $local 'remote-proof.txt') 'from-sftp'
    }
    'sftp-ui' {
      $remoteName = 'ui_sftp_' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
      Open-Menu (Find-Id $main 'ToolsMenu'); Click (Find-Id $main 'CloudEndpointManager')
      $manager = Find-WindowLike '端点管理'; Click (Find-Id $manager 'NewCloudEndpoint')
      $editor = Find-WindowLike '新建云端'; $service = Find-Id $editor 'CloudServiceType'; $expand = $null; $service.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$expand) | Out-Null; $expand.Expand(); Select-Ui (Find-Name $service 'SFTP')
      Set-Text (Find-Id $editor 'SftpRemoteName') $remoteName; Set-Text (Find-Id $editor 'SftpRemoteHost') '127.0.0.1'; Set-Text (Find-Id $editor 'SftpRemotePort') "$port"; Set-Text (Find-Id $editor 'SftpRemoteUser') 'ui'; Set-Password (Find-Id $editor 'SftpRemotePassword') $password; Set-Text (Find-Id $editor 'SftpRemoteRoot') 'docs'
      Click (Find-Id $editor 'SaveCloudEndpoint'); Wait-Until { (Find-Id $manager 'CloudEndpointStatus').Current.Name -match '已创建' } 'SFTP endpoint was not created by the UI.' 60
      Click (Find-Id $manager 'AddCloudEndpointRight')
      $upload = Join-Path $root 'ui-upload'; New-Item -ItemType Directory -Force -Path $upload | Out-Null; [IO.File]::WriteAllText((Join-Path $upload 'created-through-ui.txt'), 'endpoint-ui-proof')
      Select-Mode $main '更新 →'; Compare-Ui $main $upload (Get-Text (Find-Id $main 'RightPath')); Wait-Sync $main (Join-Path $share 'created-through-ui.txt'); Assert-File (Join-Path $share 'created-through-ui.txt') 'endpoint-ui-proof'
    }
    'profile' {
      $left = Join-Path $root 'profile-left'; $right = Join-Path $root 'profile-right'; New-Item -ItemType Directory -Force -Path @($left, $right) | Out-Null
      $profileName = 'ui-profile-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
      # New, then Edit is the same path a user follows from the Profile pane.
      Click (Find-Id $main 'NewProfileButton'); Click (Find-Id $main 'EditProfileButton')
      $editor = Find-WindowLike 'Profile'; Set-Text (Find-Id $editor 'ProfileName') $profileName; Set-Text (Find-Id $editor 'ProfileLeftPath') $left; Set-Text (Find-Id $editor 'ProfileRightPath') $right; Click (Find-Id $editor 'ProfileSave')
      Wait-Until { (Find-Id $main 'Status').Current.Name -match '已保存' } 'Profile edit did not report a save.'
      # A cancelled edit must not leak into the in-memory UI nor the persisted profile.
      Click (Find-Id $main 'EditProfileButton'); $editor = Find-WindowLike 'Profile'; Set-Text (Find-Id $editor 'ProfileName') 'must-not-persist'; Click (Find-Id $editor 'ProfileCancel')
      if ((Find-Id $main 'ProfileList').Current.Name -match 'must-not-persist') { throw 'Cancelled profile edit changed the profile list.' }
      Stop-App $process; $process = $null
      $launch = Start-App; $process = $launch[0]; $main = $launch[1]
      # Find the item, then select it through the normal keyboard interaction in
      # the Profile pane. The WPF ListBoxItem provider exposes no SelectionItem
      # pattern on this desktop, but End is the equivalent user action here.
      $item = Find-Name (Find-Id $main 'ProfileList') $profileName
      (Find-Id $main 'ProfileList').SetFocus(); [Windows.Forms.SendKeys]::SendWait('{END}')
      Wait-Until { (Get-Text (Find-Id $main 'LeftPath')) -eq $left } 'Persisted Profile did not load after selecting it.'
      if ((Get-Text (Find-Id $main 'LeftPath')) -ne $left -or (Get-Text (Find-Id $main 'RightPath')) -ne $right) { throw 'Persisted Profile endpoints did not survive application restart.' }
      Click (Find-Id $main 'DeleteProfileButton')
      $removeDialog = Find-WindowLike '移除配置'; [Windows.Forms.SendKeys]::SendWait('%y')
      Wait-Until { try { -not (Find-Name (Find-Id $main 'ProfileList') $profileName 1) } catch { $true } } 'Deleted Profile remained in the list.'
    }
    'profile-filter' {
      $left = Join-Path $root 'left'; $right = Join-Path $root 'right'; New-Item -ItemType Directory -Force -Path @($left, $right, (Join-Path $left '.git')) | Out-Null
      [IO.File]::WriteAllText((Join-Path $left 'included.txt'), 'included'); [IO.File]::WriteAllText((Join-Path $left '.git\config'), 'must-be-filtered')
      Click (Find-Id $main 'EditProfileButton'); $editor = Find-WindowLike 'Profile'; Set-Text (Find-Id $editor 'ProfileLeftPath') $left; Set-Text (Find-Id $editor 'ProfileRightPath') $right
      $sections = Find-Id $editor 'ProfileSections'; $sections.SetFocus(); [Windows.Forms.SendKeys]::SendWait('{HOME}{DOWN}{DOWN}')
      Click (Find-Name $editor '常用排除'); Click (Find-Id $editor 'ProfileSave')
      Compare-Ui $main $left $right; Wait-Sync $main (Join-Path $right 'included.txt'); Assert-File (Join-Path $right 'included.txt') 'included'
      if (Test-Path -LiteralPath (Join-Path $right '.git\config')) { throw 'Profile filter did not exclude .git content from the real sync.' }
    }
    'delete-threshold' {
      $left = Join-Path $root 'left'; $right = Join-Path $root 'right'; New-Item -ItemType Directory -Force -Path @($left, $right) | Out-Null
      [IO.File]::WriteAllText((Join-Path $left 'keep.txt'), 'keep'); [IO.File]::WriteAllText((Join-Path $right 'keep.txt'), 'keep'); [IO.File]::WriteAllText((Join-Path $right 'delete-a.txt'), 'a'); [IO.File]::WriteAllText((Join-Path $right 'delete-b.txt'), 'b')
      $profileName = 'threshold-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
      Click (Find-Id $main 'EditProfileButton'); $editor = Find-WindowLike 'Profile'; Set-Text (Find-Id $editor 'ProfileName') $profileName; Set-Text (Find-Id $editor 'ProfileLeftPath') $left; Set-Text (Find-Id $editor 'ProfileRightPath') $right
      $sections = Find-Id $editor 'ProfileSections'; $sections.SetFocus(); [Windows.Forms.SendKeys]::SendWait('{END}')
      Set-Text (Find-Id $editor 'ProfileMaxDeletes') '0'; Set-Text (Find-Id $editor 'ProfileMaxDeleteRatio') '0'; Click (Find-Id $editor 'ProfileSave')
      Select-Mode $main '镜像 →'; Compare-Ui $main $left $right; Wait-Until { (Find-Id $main 'SyncButton').Current.IsEnabled } 'Mirror threshold plan was not executable.'
      Click (Find-Id $main 'SyncButton'); $confirm = Find-WindowLike '确认同步'; Set-Text (Find-Id $confirm 'ProfileNameInput') $profileName; Click (Find-Id $confirm 'ConfirmSyncButton')
      Wait-Until { -not (Test-Path -LiteralPath (Join-Path $right 'delete-a.txt')) -and -not (Test-Path -LiteralPath (Join-Path $right 'delete-b.txt')) } 'Profile-name threshold confirmation did not permit mirror deletion.' 60
    }
    'settings' {
      Open-Menu (Find-Id $main 'ToolsMenu'); Click (Find-Id $main 'OptionsMenuItem')
      $settings = Find-WindowLike '设置'; Select-Ui (Find-Id $settings 'SettingsDefaultsTab')
      Set-Text (Find-Id $settings 'SettingsConcurrency') '3'; Set-Text (Find-Id $settings 'SettingsTimeTolerance') '7'; Set-Text (Find-Id $settings 'SettingsIncludeRules') '**/*.keep'; Set-Text (Find-Id $settings 'SettingsExcludeRules') '**/*.skip'
      Click (Find-Id $settings 'SettingsApply'); Click (Find-Id $settings 'SettingsOk')
      Wait-Until { (Find-Id $main 'Status').Current.Name -match '已应用' } 'Settings did not report apply.'
      Open-Menu (Find-Id $main 'ToolsMenu'); Click (Find-Id $main 'OptionsMenuItem'); $settings = Find-WindowLike '设置'; Select-Ui (Find-Id $settings 'SettingsDefaultsTab')
      if ((Get-Text (Find-Id $settings 'SettingsConcurrency')) -ne '3' -or (Get-Text (Find-Id $settings 'SettingsTimeTolerance')) -ne '7' -or (Get-Text (Find-Id $settings 'SettingsIncludeRules')) -ne '**/*.keep' -or (Get-Text (Find-Id $settings 'SettingsExcludeRules')) -ne '**/*.skip') { throw 'Applied application defaults were not persisted on re-open.' }
      Click (Find-Id $settings 'SettingsCancel')
    }
    'history' {
      $left = Join-Path $root 'left'; $right = Join-Path $root 'right'; New-Item -ItemType Directory -Force -Path @($left, $right) | Out-Null
      [IO.File]::WriteAllText((Join-Path $left 'recorded.txt'), 'history-proof'); Compare-Ui $main $left $right; Wait-Sync $main (Join-Path $right 'recorded.txt')
      Open-Menu (Find-Id $main 'OperationsMenu'); Click (Find-Id $main 'RunHistoryMenuItem'); $history = Find-WindowLike '历史'
      Click (Find-Id $history 'RefreshRunHistory')
      $entry = Wait-Until { $items = (Find-Id $history 'RunHistoryEntries').FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::DataItem)); if ($items.Count -gt 0) { $items[0] } } 'Run history did not show the real completed run.' 30
      $outcome = Find-Id $history 'RunHistoryOutcome'; $expand = $null; $outcome.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$expand) | Out-Null; $expand.Expand(); Select-Ui (Find-Name $outcome '成功')
      Wait-Until { (Find-Id $history 'RunHistoryEntries').FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::DataItem)).Count -gt 0 } 'Successful-outcome filter hid the completed run.'
      $history.SetFocus(); [Windows.Forms.SendKeys]::SendWait('{ESC}')
    }
    'schedule' {
      $scheduledTask = 'FengSync-Test-' + [Guid]::NewGuid().ToString('N')
      Open-Menu (Find-Id $main 'ToolsMenu'); Click (Find-Id $main 'ScheduleMenuItem'); $schedule = Find-WindowLike '计划任务'
      Set-Text (Find-Id $schedule 'ScheduleTaskName') $scheduledTask
      Click (Find-Id $schedule 'CreateScheduleButton')
      Wait-Until { & schtasks.exe /Query /TN $scheduledTask *> $null; $LASTEXITCODE -eq 0 } 'Schedule UI did not create the unique Windows task.' 30
      Click (Find-Id $schedule 'TestScheduleButton')
      Wait-Until { try { (Find-Id $schedule 'ResultText').Current.Name -match '测试运行|请求' } catch { $true } } 'Schedule UI did not request a test run.' 30
      # schtasks may refuse deletion while its just-requested action is still
      # starting. Wait for the short CLI invocation to leave Running state.
      Wait-Until { $state = & schtasks.exe /Query /TN $scheduledTask /FO LIST 2>$null; $LASTEXITCODE -eq 0 -and ($state -notmatch 'Running|正在运行') } 'Scheduled test run did not leave the running state.' 60
      Click (Find-Id $schedule 'DeleteScheduleButton')
      Wait-Until { & schtasks.exe /Query /TN $scheduledTask *> $null; $LASTEXITCODE -ne 0 } 'Schedule UI did not delete the unique Windows task.' 30
      $scheduledTask = $null
    }
    'gdrive' {
      $upload = Join-Path $root 'drive-upload'; $download = Join-Path $root 'drive-download'; New-Item -ItemType Directory -Force -Path @($upload, $download) | Out-Null
      [IO.File]::WriteAllText((Join-Path $upload 'drive-proof.txt'), 'drive-roundtrip')
      Select-Mode $main '更新 →'; Compare-Ui $main $upload $driveUri; Wait-Sync $main (Join-Path $upload 'drive-proof.txt')
      Wait-Until {
        $listing = & $rclone lsf "${driveRemote}:test/FengSync-Automated-Tests/$driveChild" --config $config 2>$null
        $LASTEXITCODE -eq 0 -and $listing -contains 'drive-proof.txt'
      } 'UI upload did not become visible in the generated Google Drive test child.' 120
      # Start a second real application session for the download. This models the
      # common upload-then-later-download workflow and avoids reusing a Drive RC
      # daemon while its previous request is still unwinding.
      Stop-App $process; $process = $null
      $launch = Start-App; $process = $launch[0]; $main = $launch[1]
      Select-Mode $main '更新 →'
      Compare-Ui $main $driveUri $download
      # Drive's second scan is performed again immediately before execution; allow
      # its bounded 2-minute RC request plus transfer time on slow Google APIs.
      Wait-Sync $main (Join-Path $download 'drive-proof.txt') 180 300
      Assert-File (Join-Path $download 'drive-proof.txt') 'drive-roundtrip'
    }
    'gdrive-volume' {
      # This is deliberately a measurement test rather than a fixed time budget:
      # Google API latency varies, while the emitted timings make file-count
      # regressions visible without turning a healthy remote run into a flaky test.
      $results = [System.Collections.Generic.List[object]]::new()
      foreach ($mode in @(
        [pscustomobject]@{ Name = '双向 ↔'; Slug = 'two-way' },
        [pscustomobject]@{ Name = '镜像 →'; Slug = 'mirror' },
        [pscustomobject]@{ Name = '更新 →'; Slug = 'update' }
      )) {
        foreach ($count in @(10, 100)) {
          $case = "$($mode.Slug)-$count"
          $upload = Join-Path $root (Join-Path 'drive-volume' $case)
          $remoteCase = $driveUri.TrimEnd('/') + '/' + $case
          # Google Drive does not preserve an empty directory. Materialize each
          # isolated case so both comparison roots exist before the UI scans them.
          & $rclone mkdir ("${driveRemote}:test/FengSync-Automated-Tests/$driveChild/$case") --config $config
          if ($LASTEXITCODE -ne 0) { throw "Could not create Google Drive performance test child: $remoteCase" }
          $anchor = Join-Path $root ("$case-anchor"); [IO.File]::WriteAllText($anchor, 'fixture')
          & $rclone copyto $anchor ("${driveRemote}:test/FengSync-Automated-Tests/$driveChild/$case/.fengsync-fixture-anchor") --config $config
          if ($LASTEXITCODE -ne 0) { throw "Could not materialize Google Drive performance test child: $remoteCase" }
          New-SmallFiles $upload $count
          Select-Mode $main $mode.Name
          $timing = Invoke-MeasuredSync $main $upload $remoteCase (Join-Path $upload 'batch-001.txt') 180 600
          Assert-GoogleDriveBatch ("${driveRemote}:test/FengSync-Automated-Tests/$driveChild/$case") $count
          $results.Add([pscustomobject]@{ Mode = $mode.Slug; Files = $count; CompareMilliseconds = $timing.CompareMilliseconds; SyncMilliseconds = $timing.SyncMilliseconds })
          Write-Output ("Google Drive performance: mode={0}, files={1}, compare={2}ms, sync={3}ms" -f $mode.Slug, $count, $timing.CompareMilliseconds, $timing.SyncMilliseconds)
        }
      }
      if ($results.Count -ne 6) { throw 'Google Drive performance matrix did not complete every mode and file-count combination.' }
    }
  }
  Capture-Window $process '99-complete.png'
  $passed = $true
  $scenarioTimer.Stop()
  Write-Output ("Passed {0}; completed in {1}" -f $Scenario, $scenarioTimer.Elapsed)
}
finally {
  Stop-App $process
  if ($remoteCleanup) {
    # The URI was validated as a child of the caller-provided dedicated test root.
    $parts = $remoteCleanup.Substring('gdrive://'.Length).Split('/',2); $config = Join-Path $appData 'rclone\rclone.conf'; $rclone = Join-Path (Split-Path $AppPath) 'Assets\rclone\rclone.exe'
    if ($parts.Count -eq 2) { & $rclone purge ($parts[0] + ':' + $parts[1]) --config $config; if ($LASTEXITCODE -ne 0) { Write-Error "Google Drive cleanup failed for generated child $remoteCleanup" } }
  }
  if ($server -and -not $server.HasExited) { $server.Kill($true); $server.WaitForExit(); $server.Dispose() }
  if ($scheduledTask) { & schtasks.exe /Delete /F /TN $scheduledTask *> $null }
  if ($passed -and (Test-Path -LiteralPath $root)) { Remove-Item -LiteralPath $root -Recurse -Force }
  elseif (Test-Path -LiteralPath $root) { Write-Output "Artifacts retained: $root" }
}

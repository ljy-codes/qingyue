[CmdletBinding()]
param(
    [string] $AssemblyDirectory = (Join-Path $PSScriptRoot '..\..\..\开发空间\Windows\tests\Qingyue.ReadingTests\bin\Release\net10.0-windows'),
    [string] $LogPath
)

# Run in a fresh PowerShell STA process. Never Show a window or pump the dispatcher.
$ErrorActionPreference = 'Stop'
$taskLines = [Collections.Generic.List[string]]::new()
$taskFailures = 0
$taskChecks = 0
$taskExitCode = 2
$taskWindow = $null
$taskApp = $null
$taskProfile = Join-Path ([IO.Path]::GetTempPath()) ('qingyue-reading-p2-' + [Guid]::NewGuid())
$taskFlags = [Reflection.BindingFlags]'Instance,NonPublic'

function Write-Probe([string] $Text) {
    $taskLines.Add($Text)
    Write-Host $Text
}

function Assert-Probe([bool] $Condition, [string] $Name, [string] $Detail) {
    $script:taskChecks++
    if ($Condition) {
        Write-Probe "PASS $Name | $Detail"
    }
    else {
        $script:taskFailures++
        Write-Probe "FAIL $Name | $Detail"
    }
}

function Get-PrivateField([string] $Name) {
    $taskField = $taskWindow.GetType().GetField($Name, $taskFlags)
    if ($null -eq $taskField) { throw "Probe integration field missing: $Name" }
    return $taskField.GetValue($taskWindow)
}

function Invoke-Private([string] $Name) {
    $taskMethod = $taskWindow.GetType().GetMethod($Name, $taskFlags)
    if ($null -eq $taskMethod) { throw "Probe integration method missing: $Name" }
    [void] $taskMethod.Invoke($taskWindow, @())
}

function Set-ProbeLayout($View) {
    $View.Measure([Windows.Size]::new(680, 400))
    $View.Arrange([Windows.Rect]::new(0, 0, 680, 400))
    $View.UpdateLayout()
}

function Get-ProbeFonts($View) {
    $taskBlocks = @($View.Document.Blocks)
    $taskCode = @($taskBlocks | Where-Object {
        $_ -is [Windows.Documents.Paragraph] -and $_.FontFamily.Source.Contains('Mono')
    })[0]
    $taskTable = @($taskBlocks | Where-Object { $_ -is [Windows.Documents.Table] })[0]
    if ($null -eq $taskCode -or $null -eq $taskTable) {
        throw 'Probe fixture must render a code paragraph and a real Table.'
    }
    return @{
        body = [double] $View.Document.FontSize
        heading = [double] $View.Document.Blocks.FirstBlock.FontSize
        code = [double] $taskCode.FontSize
        table = [double] $taskTable.FontSize
    }
}

function Test-ProbeFonts($Before, $After, [double] $ExpectedBody, [string] $Prefix) {
    $taskRatio = $ExpectedBody / $Before.body
    foreach ($taskName in @('body', 'heading', 'code', 'table')) {
        $taskExpected = $Before[$taskName] * $taskRatio
        Assert-Probe ([Math]::Abs($After[$taskName] - $taskExpected) -lt 0.01) `
            "$Prefix.$taskName" ("before={0}; actual={1}; expected={2}" -f `
            $Before[$taskName], $After[$taskName], $taskExpected)
    }
}

function Test-ProbeSplit([string] $Change) {
    # Each scenario starts with its own document and nonempty selection.
    $taskSource = "# Heading`n`n~~~cs`nint value = 1;`n~~~`n`n| A | B |`n| --- | --- |`n| x | y |`n`n" +
        ((1..160 | ForEach-Object {
            'Paragraph {0}: content to retain while reflowing the reading pane.' -f $_
        }) -join "`n`n")
    $taskTab = $taskWindow.NewDocument($taskSource)
    Invoke-Private 'ToggleMarkdownPreview'
    $taskOld = Get-PrivateField 'markdownPreview'
    if ($null -eq $taskOld) { throw 'Probe fixture did not enter split preview.' }
    Set-ProbeLayout $taskOld
    $taskParagraph = @($taskOld.Document.Blocks)[30]
    $taskStart = $taskParagraph.ContentStart
    $taskOld.Selection.Select($taskStart, $taskStart.GetPositionAtOffset(9))
    $taskOld.ScrollToVerticalOffset(800)
    $taskOld.UpdateLayout()
    $taskSelected = $taskOld.Selection.Text
    $taskOffset = $taskOld.VerticalOffset
    $taskDocument = $taskOld.Document
    $taskSelectionStart = $taskDocument.ContentStart.GetOffsetToPosition($taskOld.Selection.Start)
    $taskSelectionEnd = $taskDocument.ContentStart.GetOffsetToPosition($taskOld.Selection.End)
    $taskFonts = Get-ProbeFonts $taskOld
    if ($taskSelected.Length -eq 0 -or $taskOffset -lt 100 -or $taskTab.IsReading) {
        throw 'Probe split fixture did not establish a selection and scrolled viewport.'
    }
    Write-Probe ("BEFORE split-{0}: scroll={1}; selected={2}; font={3}; width={4}" -f `
        $Change, $taskOffset, $taskSelected.Length, $taskSettings.FontSize, $taskSettings.ReadingWidth)
    if ($Change -eq 'width') {
        $taskSettings.ReadingWidth += 100
        Invoke-Private 'UpdateReadingWidths'
    }
    else {
        $taskSettings.FontSize += 4
        Invoke-Private 'ApplyEditorSettings'
    }
    $taskNew = Get-PrivateField 'markdownPreview'
    Set-ProbeLayout $taskNew
    Write-Probe ("AFTER split-{0}: scroll={1}; selected={2}; previewReplaced={3}; documentReplaced={4}" -f `
        $Change, $taskNew.VerticalOffset, $taskNew.Selection.Text.Length,
        (-not [Object]::ReferenceEquals($taskOld, $taskNew)),
        (-not [Object]::ReferenceEquals($taskDocument, $taskNew.Document)))
    Assert-Probe ([Object]::ReferenceEquals($taskOld, $taskNew)) "P2-split-$Change.same-preview" 'preserve control identity'
    Assert-Probe ([Object]::ReferenceEquals($taskDocument, $taskNew.Document)) "P2-split-$Change.same-document" 'preserve FlowDocument identity'
    $taskNewStart = $taskNew.Document.ContentStart.GetOffsetToPosition($taskNew.Selection.Start)
    $taskNewEnd = $taskNew.Document.ContentStart.GetOffsetToPosition($taskNew.Selection.End)
    Assert-Probe ($taskSelected -ceq $taskNew.Selection.Text -and
        $taskSelectionStart -eq $taskNewStart -and $taskSelectionEnd -eq $taskNewEnd) "P2-split-$Change.selection" `
        ("beforeLength={0}; afterLength={1}; beforeRange={2}..{3}; afterRange={4}..{5}" -f `
        $taskSelected.Length, $taskNew.Selection.Text.Length, $taskSelectionStart, $taskSelectionEnd, $taskNewStart, $taskNewEnd)
    Assert-Probe ([Math]::Abs($taskOffset - $taskNew.VerticalOffset) -lt 1) "P2-split-$Change.scroll" `
        ("before={0}; after={1}; tolerance=1 DIP" -f $taskOffset, $taskNew.VerticalOffset)
    Assert-Probe ($taskTab.File.Text -ceq $taskSource) "P2-split-$Change.source" 'synthetic source unchanged'
    if ($Change -eq 'font') {
        Test-ProbeFonts $taskFonts (Get-ProbeFonts $taskNew) ($taskSettings.FontSize + 1) 'P2-split-font.scale'
    }
}

try {
    $AssemblyDirectory = (Resolve-Path -LiteralPath $AssemblyDirectory).Path
    Write-Probe 'Qingyue reading P2 headless behavior probe'
    Write-Probe ('Timestamp: ' + [DateTimeOffset]::Now.ToString('o'))
    Write-Probe ('Command: pwsh -NoProfile -STA -File "{0}" -AssemblyDirectory "{1}"{2}' -f `
        $PSCommandPath, $AssemblyDirectory, $(if ($LogPath) { ' -LogPath "' + $LogPath + '"' } else { '' }))
    Write-Probe ("Environment: PowerShell={0}; .NET={1}; OS={2}; apartment={3}" -f `
        $PSVersionTable.PSVersion, [Environment]::Version, [Environment]::OSVersion,
        [Threading.Thread]::CurrentThread.ApartmentState)
    if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Use pwsh -STA.' }
    Add-Type -AssemblyName PresentationFramework
    foreach ($taskDllName in @('Qingyue.Core.dll', 'Qingyue.dll')) {
        $taskDll = Get-Item -LiteralPath (Join-Path $AssemblyDirectory $taskDllName)
        $taskHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $taskDll.FullName).Hash
        Write-Probe ("Assembly: {0}; bytes={1}; modified={2}; SHA256={3}" -f `
            $taskDll.FullName, $taskDll.Length, $taskDll.LastWriteTime.ToString('o'), $taskHash)
        [void] [Reflection.Assembly]::LoadFrom($taskDll.FullName)
    }
    $taskApp = [Windows.Application]::new()
    $taskApp.ShutdownMode = [Windows.ShutdownMode]::OnExplicitShutdown
    # Without a UI context, QueueOutline's async continuation could run on a pool
    # thread. Do not confuse that invalid test host with a product failure.
    [Threading.SynchronizationContext]::SetSynchronizationContext(
        [Windows.Threading.DispatcherSynchronizationContext]::new(
            [Windows.Threading.Dispatcher]::CurrentDispatcher))
    $taskTheme = [Windows.ResourceDictionary]::new()
    $taskTheme.Source = [Uri]::new('pack://application:,,,/Qingyue;component/Theme.xaml')
    $taskApp.Resources.MergedDictionaries.Add($taskTheme)
    $taskWindow = [Qingyue.Windows.MainWindow]::new($taskProfile, $false)
    $taskSettings = Get-PrivateField 'settings'

    $taskSource = "# Heading`n`nBody`n`n~~~cs`nint value = 1;`n~~~`n`n| A | B |`n| --- | --- |`n| x | y |"
    $taskTab = $taskWindow.NewDocument($taskSource)
    $taskWindow.ToggleReading()
    if (-not $taskTab.IsReading) { throw 'Probe fixture did not enter full reading.' }
    $taskDocument = $taskTab.Preview.Document
    $taskBefore = Get-ProbeFonts $taskTab.Preview
    $taskSettings.FontSize += 4
    Invoke-Private 'ApplyEditorSettings'
    $taskAfter = Get-ProbeFonts $taskTab.Preview
    Test-ProbeFonts $taskBefore $taskAfter ($taskSettings.FontSize + 1) 'P2-full-font'
    Assert-Probe ([Object]::ReferenceEquals($taskDocument, $taskTab.Preview.Document)) `
        'P2-full-font.same-document' 'preserve FlowDocument identity'
    Assert-Probe ($taskTab.File.Text -ceq $taskSource) 'P2-full-font.source' 'synthetic source unchanged'

    Test-ProbeSplit 'width'
    Test-ProbeSplit 'font'
    Assert-Probe (-not $taskWindow.IsVisible) 'host.hidden' 'Window.Show was never called'
    Write-Probe ('Context: ' + [Threading.SynchronizationContext]::Current.GetType().Name)
    $taskExitCode = $(if ($taskFailures -eq 0) { 0 } else { 1 })
}
catch {
    $taskExitCode = 2
    Write-Probe ('HOST ERROR: ' + $_.Exception.ToString())
    Write-Probe ('Position: ' + $_.InvocationInfo.PositionMessage)
}
finally {
    try {
        if ($null -ne $taskWindow) { $taskWindow.CloseForShutdown() }
        if ($null -ne $taskApp) { $taskApp.Shutdown() }
        $taskProfileWritten = Test-Path -LiteralPath $taskProfile
        Write-Probe ("Host: profileWritten={0}; visibleWindow={1}; profile={2}" -f `
            $taskProfileWritten, ($null -ne $taskWindow -and $taskWindow.IsVisible), $taskProfile)
        if ($taskProfileWritten) {
            Write-Probe 'HOST ERROR: unexpected profile persistence'
            $taskExitCode = 2
        }
    }
    catch {
        Write-Probe ('HOST ERROR during cleanup: ' + $_.Exception.ToString())
        $taskExitCode = 2
    }
    Write-Probe ("RESULT: checks={0}; behaviorFailures={1}; exitCode={2} (0=green, 1=behavior red, 2=host error)" -f `
        $taskChecks, $taskFailures, $taskExitCode)
    if ($LogPath) {
        [IO.File]::WriteAllLines([IO.Path]::GetFullPath($LogPath), $taskLines, [Text.UTF8Encoding]::new($false))
    }
}
exit $taskExitCode

param(
  [int]$ProcessId,
  [string]$SetPort,
  [string]$Invoke
)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
$windows = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
$all = @()
foreach ($w in $windows) { $all += $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) }

if ($SetPort) {
  $edits = @($all | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Edit })
  if ($edits.Count -lt 1) { throw 'No edit control found' }
  $portBox = $edits[0]
  $value = $portBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
  $value.SetValue($SetPort)
  Write-Output "Port set to $SetPort"
}

if ($Invoke) {
  $button = @($all | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and $_.Current.Name -eq $Invoke } | Select-Object -First 1)
  if ($button.Count -eq 0) { throw "Button '$Invoke' not found" }
  $invokePattern = $button[0].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
  $invokePattern.Invoke()
  Write-Output "Invoked $Invoke"
}

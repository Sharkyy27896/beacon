param([int]$ProcessId, [int]$Depth = 4)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$root = [System.Windows.Automation.AutomationElement]::RootElement
$condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId)
$windows = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $condition)
foreach ($w in $windows) {
  Write-Output ("WINDOW name='{0}' class='{1}' rect={2} offscreen={3}" -f $w.Current.Name, $w.Current.ClassName, $w.Current.BoundingRectangle, $w.Current.IsOffscreen)
  function Walk($el, $level) {
    if ($level -gt $Depth) { return }
    $children = $el.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($c in $children) {
      $cName = $c.Current.Name
      if ($cName.Length -gt 90) { $cName = $cName.Substring(0,90) }
      Write-Output (("{0}- {1}: '{2}' rect={3}" -f (' ' * ($level * 2)), $c.Current.ControlType.ProgrammaticName, $cName, $c.Current.BoundingRectangle))
      Walk $c ($level + 1)
    }
  }
  Walk $w 1
}

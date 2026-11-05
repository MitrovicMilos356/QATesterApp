# Test fixture for selector generation: a WinForms window with the cases a selector generator
# must handle. It opens off-screen (no monitor there) so tests never cover the user's screen;
# UI Automation reads it the same either way. Clicking Save sets the status label to "Saved",
# which lets an end-to-end check prove that generated code really acted on the right button.
param([int]$X = -3000, [int]$Y = -3000, [string]$Title = 'UiInspector Selector Fixture', [string]$FormName = 'SelectorFixtureForm')

Add-Type -AssemblyName System.Windows.Forms

function New-Button([string]$text, [string]$name, [int]$x, [int]$y) {
    $b = New-Object System.Windows.Forms.Button
    $b.Text = $text; $b.Name = $name; $b.Location = New-Object System.Drawing.Point($x, $y); $b.Width = 90
    $b
}

function New-Panel([string]$name, [int]$x, [int]$y, [int]$w, [int]$h) {
    $p = New-Object System.Windows.Forms.Panel
    $p.Name = $name; $p.Location = New-Object System.Drawing.Point($x, $y); $p.Size = New-Object System.Drawing.Size($w, $h)
    $p.BorderStyle = 'FixedSingle'
    $p
}

$form = New-Object System.Windows.Forms.Form
$form.Text = $Title
$form.Name = $FormName
$form.StartPosition = 'Manual'
$form.Location = New-Object System.Drawing.Point($X, $Y)
$form.Size = New-Object System.Drawing.Size(540, 420)
$form.ShowInTaskbar = $false

$status = New-Object System.Windows.Forms.Label
$status.Text = 'Status: 3 items'; $status.Location = New-Object System.Drawing.Point(10, 250); $status.AutoSize = $true

# Unique, well-named control: AutomationId = WinForms Name.
$save = New-Button 'Save' 'saveButton' 10 10
$save.Add_Click({ $status.Text = 'Saved' })

# Each button below reports itself in the status label, so end-to-end checks can tell
# exactly which of several look-alike buttons generated code clicked.
function Add-Reporter($button, [string]$report) {
    $button.Tag = $report
    $button.Add_Click({ param($sender) $status.Text = $sender.Tag })
    $button
}

# Same caption in two panels: needs the panel as an anchor.
$orders = New-Panel 'ordersPanel' 10 50 250 90
$orders.Controls.Add((Add-Reporter (New-Button 'Delete' '' 10 10) 'Deleted order'))
$customers = New-Panel 'customersPanel' 270 50 250 90
$customers.Controls.Add((Add-Reporter (New-Button 'Delete' '' 10 10) 'Deleted customer'))

# Three identical, unnamed buttons: only their position tells them apart.
$tools = New-Panel 'toolsPanel' 10 150 510 80
foreach ($i in 0..2) { $tools.Controls.Add((Add-Reporter (New-Button 'Action' '' (10 + $i * 100) 10) "Action $($i + 1) clicked")) }

$notes = New-Object System.Windows.Forms.TextBox
$notes.Name = 'notesBox'; $notes.Location = New-Object System.Drawing.Point(10, 280); $notes.Width = 300

# For the recorder: a checkbox (Toggle) and a password field. A password can't be read through
# UI Automation, so a label reports its length for end-to-end checks.
$remember = New-Object System.Windows.Forms.CheckBox
$remember.Name = 'rememberBox'; $remember.Text = 'Remember me'; $remember.Location = New-Object System.Drawing.Point(10, 315); $remember.AutoSize = $true

$password = New-Object System.Windows.Forms.TextBox
$password.Name = 'passwordBox'; $password.UseSystemPasswordChar = $true; $password.Location = New-Object System.Drawing.Point(320, 280); $password.Width = 190
$passwordInfo = New-Object System.Windows.Forms.Label
$passwordInfo.Name = 'passwordInfo'; $passwordInfo.Text = 'Password length 0'; $passwordInfo.Location = New-Object System.Drawing.Point(320, 315); $passwordInfo.AutoSize = $true
$password.Add_TextChanged({ $passwordInfo.Text = "Password length $($password.Text.Length)" })

$form.Controls.AddRange(@($save, $orders, $customers, $tools, $status, $notes, $remember, $password, $passwordInfo))
[System.Windows.Forms.Application]::Run($form)

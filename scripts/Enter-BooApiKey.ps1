Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()
$workspacePath = Join-Path $env:USERPROFILE 'Downloads\blue--fo'
$targetPath = Join-Path $workspacePath '.env.local'
$statusDirectory = Join-Path $workspacePath 'artifacts\setup'
[IO.Directory]::CreateDirectory($statusDirectory) | Out-Null
$statusPath = Join-Path $statusDirectory 'key-entry-status.json'
[IO.File]::WriteAllText($statusPath, '{"status":"waiting"}')
$form = New-Object Windows.Forms.Form
$form.Text = 'Boo - Local API key setup'
$form.Size = New-Object Drawing.Size(600,330)
$form.StartPosition = 'CenterScreen'
$form.FormBorderStyle = 'FixedDialog'
$form.MaximizeBox = $false
$form.MinimizeBox = $false
$form.TopMost = $true
$form.Font = New-Object Drawing.Font('Segoe UI',10)
$heading = New-Object Windows.Forms.Label
$heading.Text = 'Paste your OpenAI API key here'
$heading.Location = New-Object Drawing.Point(24,20)
$heading.Size = New-Object Drawing.Size(545,28)
$heading.Font = New-Object Drawing.Font('Segoe UI',13,[Drawing.FontStyle]::Bold)
$form.Controls.Add($heading)
$help = New-Object Windows.Forms.Label
$help.Text = "This box is local to your PC. The key is masked and is not sent to chat.`r`nSave locally confirms writing OPENAI_API_KEY to the file shown below."
$help.Location = New-Object Drawing.Point(25,59)
$help.Size = New-Object Drawing.Size(540,46)
$form.Controls.Add($help)
$keyBox = New-Object Windows.Forms.TextBox
$keyBox.Location = New-Object Drawing.Point(25,116)
$keyBox.Size = New-Object Drawing.Size(535,30)
$keyBox.UseSystemPasswordChar = $true
$keyBox.AccessibleName = 'OpenAI API key (masked local input)'
$form.Controls.Add($keyBox)
$destination = New-Object Windows.Forms.Label
$destination.Text = "Destination (ignored by Git):`r`n$targetPath"
$destination.Location = New-Object Drawing.Point(25,157)
$destination.Size = New-Object Drawing.Size(535,43)
$destination.Font = New-Object Drawing.Font('Segoe UI',9)
$form.Controls.Add($destination)
$feedback = New-Object Windows.Forms.Label
$feedback.Location = New-Object Drawing.Point(25,204)
$feedback.Size = New-Object Drawing.Size(535,22)
$feedback.ForeColor = [Drawing.Color]::DarkRed
$form.Controls.Add($feedback)
$save = New-Object Windows.Forms.Button
$save.Text = 'Save locally'
$save.Location = New-Object Drawing.Point(321,243)
$save.Size = New-Object Drawing.Size(130,32)
$form.Controls.Add($save)
$cancel = New-Object Windows.Forms.Button
$cancel.Text = 'Cancel'
$cancel.Location = New-Object Drawing.Point(462,243)
$cancel.Size = New-Object Drawing.Size(98,32)
$form.Controls.Add($cancel)
$form.AcceptButton = $save
$form.CancelButton = $cancel
$save.Add_Click({
  $candidate = $keyBox.Text.Trim()
  if($candidate -notmatch '^sk-[A-Za-z0-9_-]{20,}$') { $feedback.Text = 'That does not look like an OpenAI API key. Please check it.'; return }
  try {
    if(Test-Path -LiteralPath $targetPath) {
      if((Get-Item -LiteralPath $targetPath).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Unsafe destination' }
      $existing = [IO.File]::ReadAllText($targetPath)
      if($existing -match '(?m)^\s*OPENAI_API_KEY\s*=') {
        $confirmation = [Windows.Forms.MessageBox]::Show('Replace the existing OPENAI_API_KEY in the displayed local file?','Confirm replacement',[Windows.Forms.MessageBoxButtons]::YesNo)
        if($confirmation -ne [Windows.Forms.DialogResult]::Yes) { return }
      }
    } else { $existing = '' }
    $retained = @($existing -split '\r?\n' | Where-Object { $_ -notmatch '^\s*OPENAI_API_KEY\s*=' -and $_ -ne '' })
    $retained += 'OPENAI_API_KEY=' + $candidate
    [IO.File]::WriteAllText($targetPath, (($retained -join "`r`n") + "`r`n"), (New-Object Text.UTF8Encoding($false)))
    $keyBox.Clear()
    $candidate = $null
    $retained = $null
    $existing = $null
    [IO.File]::WriteAllText($statusPath, '{"status":"saved","userConfirmedLocalWrite":true,"envName":"OPENAI_API_KEY"}')
    $form.Tag = 'saved'
    $form.Close()
  } catch {
    $candidate = $null
    $feedback.Text = 'The key could not be saved. Check the local file permissions and try again.'
  }
})
$cancel.Add_Click({$form.Close()})
$form.Add_Shown({$keyBox.Focus()})
$form.Add_FormClosed({
  $keyBox.Clear()
  if($form.Tag -ne 'saved') { [IO.File]::WriteAllText($statusPath, '{"status":"cancelled"}') }
})
[void]$form.ShowDialog()
$form.Dispose()

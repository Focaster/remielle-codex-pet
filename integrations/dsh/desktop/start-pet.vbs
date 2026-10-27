' Remielle desktop pet launcher (no console window)
' Prefers the compiled exe; falls back to the PowerShell/WPF version.
Set fso = CreateObject("Scripting.FileSystemObject")
dir = fso.GetParentFolderName(WScript.ScriptFullName)
Set sh = CreateObject("WScript.Shell")
exe = dir & "\remielle-pet.exe"
If fso.FileExists(exe) Then
  sh.Run """" & exe & """", 0, False
Else
  sh.Run "powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -WindowStyle Hidden -File """ & dir & "\pet.ps1""", 0, False
End If

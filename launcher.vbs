Option Explicit
Dim shell, base, exe
Set shell = CreateObject("WScript.Shell")
base = CreateObject("Scripting.FileSystemObject").GetParentFolderName(WScript.ScriptFullName)
exe = Chr(34) & base & "\ClashCompatibilityMonitor.exe" & Chr(34)
shell.Run exe, 0, False

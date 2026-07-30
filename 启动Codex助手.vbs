Option Explicit
Dim shell, fileSystem, projectDirectory, command
Set shell = CreateObject("WScript.Shell")
Set fileSystem = CreateObject("Scripting.FileSystemObject")
projectDirectory = fileSystem.GetParentFolderName(WScript.ScriptFullName)
command = "pwsh.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File """ & projectDirectory & "\scripts\start-bridge.ps1"""
shell.Run command, 0, False

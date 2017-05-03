set IL_MERGE="D:\Program Files (x86)\Microsoft\ILMerge\ilmerge.exe"
set TARGET_PLATFORM=C:\Windows\Microsoft.NET\Framework64\v4.0.30319
set OUTPUT_EXE=Download\FileTransfer.exe
set INPUT_EXE=Source\bin\Release\FileTransfer.exe
set INPUT_DLL=Source\bin\Release\Lidgren.Network.dll

%IL_MERGE% /targetplatform:v4,%TARGET_PLATFORM% /out:%OUTPUT_EXE% %INPUT_EXE% %INPUT_DLL%
pause
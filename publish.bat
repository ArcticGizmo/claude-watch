@echo off
dotnet publish -c Release -r win-x64 --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:EnableCompressionInSingleFile=true ^
    -p:DebugType=embedded

echo.
echo Output: bin\Release\net10.0-windows\win-x64\publish\ClaudeWatch.exe

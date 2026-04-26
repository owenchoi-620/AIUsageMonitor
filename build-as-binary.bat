REM publish path : bin/Release/net10.0-windows10.0.19041.0/win-x64/publish/

dotnet publish ./AIUsageMonitor.csproj ^
 -f net10.0-windows10.0.19041.0 ^
 -c Release ^
 -p:RuntimeIdentifierOverride=win-x64 ^
 -p:WindowsPackageType=None ^
 -p:PublishSingleFile=true ^
 -p:IncludeNativeLibrariesForSelfExtract=true ^
 -p:DebugType=None ^
 -p:DebugSymbols=false
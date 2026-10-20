@echo off
rem PiperTrainer 无 SDK 构建脚本：VS BuildTools Roslyn csc 直编（.NET 6 / IL2CPP interop）
rem 产物 build\PiperTrainer.dll 并自动部署到游戏 BepInEx\plugins\
setlocal
set CSC=%ProgramFiles(x86)%\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe
set GAME=E:\amei\The Piper Of Dawn
set NET=C:\Program Files\dotnet\shared\Microsoft.NETCore.App\6.0.36
if not exist "%CSC%" echo [x] 未找到 Roslyn csc：%CSC% & exit /b 1
if not exist "%GAME%\BepInEx\core\BepInEx.Core.dll" echo [x] 未找到游戏 BepInEx：改脚本里 GAME 变量 & exit /b 1

mkdir build 2>nul
"%CSC%" -nologo -noconfig -nostdlib+ -target:library -langversion:10 -nullable- -warn:0 ^
 -out:build\PiperTrainer.dll ^
 -r:"%NET%\System.Private.CoreLib.dll" -r:"%NET%\System.Runtime.dll" -r:"%NET%\System.Collections.dll" ^
 -r:"%NET%\System.Linq.dll" -r:"%NET%\System.ObjectModel.dll" -r:"%NET%\System.Console.dll" ^
 -r:"%NET%\netstandard.dll" -r:"%NET%\Microsoft.CSharp.dll" ^
 -r:"%GAME%\BepInEx\core\BepInEx.Core.dll" -r:"%GAME%\BepInEx\core\BepInEx.Unity.IL2CPP.dll" ^
 -r:"%GAME%\BepInEx\core\0Harmony.dll" -r:"%GAME%\BepInEx\core\Il2CppInterop.Runtime.dll" ^
 -r:"%GAME%\BepInEx\interop\Il2Cppmscorlib.dll" -r:"%GAME%\BepInEx\interop\Assembly-CSharp.dll" ^
 -r:"%GAME%\BepInEx\interop\UnityEngine.CoreModule.dll" -r:"%GAME%\BepInEx\interop\UnityEngine.IMGUIModule.dll" ^
 -r:"%GAME%\BepInEx\interop\UnityEngine.InputLegacyModule.dll" -r:"%GAME%\BepInEx\interop\Unity.TextMeshPro.dll" ^
 -r:"%GAME%\BepInEx\interop\UnityEngine.UIModule.dll" -r:"%GAME%\BepInEx\interop\UnityEngine.UI.dll" ^
 -r:"%GAME%\BepInEx\interop\UnityEngine.TextRenderingModule.dll" ^
 Plugin.cs TrainerUI.cs PiperBridge.cs PiperNames.cs UguiInput.cs StaffLive.cs ManagerSweep.cs
if errorlevel 1 echo [x] 编译失败 & exit /b 1

copy /y build\PiperTrainer.dll "%GAME%\BepInEx\plugins\PiperTrainer.dll" >nul
echo [ok] 编译并部署完成: build\PiperTrainer.dll -^> %GAME%\BepInEx\plugins\
endlocal

@echo off
echo 正在构建SAM项目...

:: 尝试查找Visual Studio 2022的MSBuild
if exist "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" (
    "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" SAM.sln /t:Rebuild /p:Configuration=Release
    goto :done
)

:: 尝试查找Visual Studio 2019的MSBuild
if exist "C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe" (
    "C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe" SAM.sln /t:Rebuild /p:Configuration=Release
    goto :done
)

:: 尝试查找Visual Studio 2017的MSBuild
if exist "C:\Program Files (x86)\Microsoft Visual Studio\2017\Community\MSBuild\15.0\Bin\MSBuild.exe" (
    "C:\Program Files (x86)\Microsoft Visual Studio\2017\Community\MSBuild\15.0\Bin\MSBuild.exe" SAM.sln /t:Rebuild /p:Configuration=Release
    goto :done
)

echo 未找到MSBuild，请确保已安装Visual Studio。
goto :end

:done
echo 构建完成。
echo 检查bin\Release目录中的SAM.exe文件。

:end
pause 
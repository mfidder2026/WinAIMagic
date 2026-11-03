@echo off
rem Builds the tiered engine with CUDA into build\tiered-cuda. Usage: build-tiered-cuda.bat [cuda architectures, default 120]
setlocal
set "ROOT=%~dp0.."
set "ARCHS=%~1"
if "%ARCHS%"=="" set "ARCHS=120"
set "PATH=%PATH:"=%"
call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat" || exit /b 1
set "SRC=%ROOT%\engine\tiered"
set "OUT=%ROOT%\build\tiered-cuda"
cmake -G Ninja -S "%SRC%" -B "%OUT%" -DCMAKE_BUILD_TYPE=Release -DSTRATA_ENABLE_CUDA=ON "-DCMAKE_CUDA_ARCHITECTURES=%ARCHS%" -DSTRATA_NATIVE_EXPERTS=ON -DSTRATA_BUILD_TESTS=OFF -DSTRATA_PORTABLE=ON "-DSTRATA_GGML_DIR=%SRC%\third_party\llama.cpp" || exit /b 1
cmake --build "%OUT%" --target maya maya-ds4 maya-mm3 -j 10 || exit /b 1
echo BUILD OK

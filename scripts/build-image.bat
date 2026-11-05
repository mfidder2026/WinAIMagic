@echo off
rem Builds the image engine into build\image-<backend>. Usage: build-image.bat cuda|vulkan|cpu
rem CUDA: real code per generation of card. The engine's own list stopped with "unsupported toolchain" on an RTX 5060 Ti (compute capability 12.0).
setlocal
set "ROOT=%~dp0.."
set "BACKEND=%~1"
set "FLAGS="
if /i "%BACKEND%"=="cuda" set FLAGS=-DSD_CUDA=ON "-DCMAKE_CUDA_ARCHITECTURES=75;86;89;120"
if /i "%BACKEND%"=="vulkan" set "FLAGS=-DSD_VULKAN=ON"
if "%BACKEND%"=="" (echo usage: build-image.bat cuda^|vulkan^|cpu & exit /b 2)
set "PATH=%PATH:"=%"
call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat" || exit /b 1
set "OUT=%ROOT%\build\image-%BACKEND%"
cmake -G Ninja -S "%ROOT%\engine\image-engine" -B "%OUT%" -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=OFF -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded -DGGML_NATIVE=OFF -DGGML_AVX=ON -DGGML_AVX2=ON -DGGML_FMA=ON -DGGML_F16C=ON -DGGML_BMI2=ON -DSD_BUILD_SHARED_LIBS=OFF %FLAGS% || exit /b 1
cmake --build "%OUT%" --target sd-cli -j 10 || exit /b 1
echo BUILD OK

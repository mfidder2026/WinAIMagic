@echo off
rem Builds the speech engine into build\speech-<backend>. Usage: build-speech.bat cuda|vulkan|cpu
rem CUDA: real code per generation of card, as for the image engine.
setlocal
set "ROOT=%~dp0.."
set "BACKEND=%~1"
set "FLAGS="
if /i "%BACKEND%"=="cuda" set FLAGS=-DGGML_CUDA=ON "-DCMAKE_CUDA_ARCHITECTURES=75;86;89;120"
if /i "%BACKEND%"=="vulkan" set "FLAGS=-DGGML_VULKAN=ON"
if "%BACKEND%"=="" (echo usage: build-speech.bat cuda^|vulkan^|cpu & exit /b 2)
set "PATH=%PATH:"=%"
call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat" || exit /b 1
set "OUT=%ROOT%\build\speech-%BACKEND%"
cmake -G Ninja -S "%ROOT%\engine\speech-engine" -B "%OUT%" -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=OFF -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded -DWHISPER_BUILD_TESTS=OFF -DWHISPER_BUILD_SERVER=OFF -DWHISPER_CURL=OFF -DGGML_NATIVE=OFF -DGGML_AVX=ON -DGGML_AVX2=ON -DGGML_FMA=ON -DGGML_F16C=ON -DGGML_BMI2=ON %FLAGS% || exit /b 1
cmake --build "%OUT%" --target whisper-cli -j 10 || exit /b 1
echo BUILD OK

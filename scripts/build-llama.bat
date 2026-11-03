@echo off
rem Builds the second engine into build\llama-<backend>. Usage: build-llama.bat cuda|vulkan|cpu
setlocal
set "ROOT=%~dp0.."
set "BACKEND=%~1"
set "FLAGS="
if /i "%BACKEND%"=="cuda" set "FLAGS=-DGGML_CUDA=ON"
if /i "%BACKEND%"=="vulkan" set "FLAGS=-DGGML_VULKAN=ON"
if /i "%BACKEND%"=="cpu" set "FLAGS="
if "%BACKEND%"=="" (echo usage: build-llama.bat cuda^|vulkan^|cpu & exit /b 2)
set "PATH=%PATH:"=%"
call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat" || exit /b 1
set "OUT=%ROOT%\build\llama-%BACKEND%"
cmake -G Ninja -S "%ROOT%\engine\llama.cpp" -B "%OUT%" -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=OFF -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded -DGGML_NATIVE=OFF -DGGML_AVX2=ON -DGGML_RPC=ON -DLLAMA_BUILD_TESTS=OFF -DLLAMA_BUILD_EXAMPLES=OFF -DLLAMA_BUILD_TOOLS=ON -DLLAMA_BUILD_SERVER=ON -DLLAMA_CURL=OFF %FLAGS% || exit /b 1
cmake --build "%OUT%" --target llama-server llama-bench llama-perplexity ggml-rpc-server -j 10 || exit /b 1
echo BUILD OK

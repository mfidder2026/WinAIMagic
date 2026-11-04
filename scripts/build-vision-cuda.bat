@echo off
rem Builds the image encoder (wam-vision.exe) with CUDA into build\vision-cuda.
rem Usage: build-vision-cuda.bat ["cuda architectures", default 120]; the package's, in quotes: build-vision-cuda.bat "75;80;86;89;120"
rem The GLM vision tower and the encoder's float32 sums are patched into the fetched llama.cpp first; both only count for this
rem exe, the engines compile the same files as before (engine\tiered\tools\vision\ggml_vision_precision.py).
setlocal
set "ROOT=%~dp0.."
set "ARCHS=%~1"
if "%ARCHS%"=="" set "ARCHS=120"
set "PATH=%PATH:"=%"
call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat" || exit /b 1
set "SRC=%ROOT%\engine\tiered"
set "LLAMA=%SRC%\third_party\llama.cpp"
set "OUT=%ROOT%\build\vision-cuda"
python "%SRC%\tools\vision\glm5next_patch.py" "%LLAMA%" || exit /b 1
cmake -G Ninja -S "%SRC%\tools\vision" -B "%OUT%" -DCMAKE_BUILD_TYPE=Release -DSTRATA_VISION_CUDA=ON "-DCMAKE_CUDA_ARCHITECTURES=%ARCHS%" -DSTRATA_PORTABLE=ON "-DLLAMA_DIR:PATH=%LLAMA%" || exit /b 1
cmake --build "%OUT%" --target wam-vision -j 10 || exit /b 1
echo BUILD OK

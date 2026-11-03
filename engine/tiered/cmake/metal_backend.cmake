# Wam's Metal build (Apple Silicon): wam-metal runs GLM-5.3-Flash through the pinned llama.cpp's ggml with its Metal
# backend.  It needs none of the CUDA/HIP engine and none of the x86 CPU kernels (AVX-512/AVX2, cpuid), so this build
# is its own short list of targets; the top-level CMakeLists.txt returns right after including it.
#
#   -DWAM_ENABLE_METAL=ON            this build
#   -DWAM_GGML_DIR=<llama.cpp>       a checkout at the pinned commit (default: fetched)
#   -DWAM_METAL_TESTS=OFF            leave out the parity tests (the setup does)
#   -DWAM_METAL_PORTABLE=ON          no -mcpu=native (a build for other Macs)
if(NOT APPLE)
  message(FATAL_ERROR "WAM_ENABLE_METAL builds for macOS on Apple Silicon")
endif()
enable_language(C OBJC)

option(WAM_METAL_TESTS "Build wam-metal's parity tests" ON)
option(WAM_METAL_PORTABLE "Build wam-metal for any Apple Silicon Mac" OFF)
set(WAM_GGML_DIR "" CACHE PATH "llama.cpp checkout to take ggml from (empty: fetch the pinned commit)")

set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
set(GGML_STATIC ON CACHE BOOL "" FORCE)
set(GGML_METAL ON CACHE BOOL "" FORCE)
set(GGML_METAL_EMBED_LIBRARY ON CACHE BOOL "" FORCE)   # the shaders live inside the binary: nothing to install
set(GGML_CUDA OFF CACHE BOOL "" FORCE)
set(GGML_OPENMP OFF CACHE BOOL "" FORCE)
set(GGML_BACKEND_DL OFF CACHE BOOL "" FORCE)
set(GGML_BUILD_TESTS OFF CACHE BOOL "" FORCE)
set(GGML_BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
if(WAM_METAL_PORTABLE)
  set(GGML_NATIVE OFF CACHE BOOL "" FORCE)
else()
  set(GGML_NATIVE ON CACHE BOOL "" FORCE)
endif()

if(WAM_GGML_DIR)
  set(_wam_llamacpp_dir ${WAM_GGML_DIR})
else()
  include(FetchContent)
  FetchContent_Declare(wam_llamacpp
    GIT_REPOSITORY https://github.com/ggml-org/llama.cpp.git
    GIT_TAG 3cf03257f219afbe7334045ff7c6a06ac68c627d
    GIT_SHALLOW FALSE)
  FetchContent_GetProperties(wam_llamacpp)
  if(NOT wam_llamacpp_POPULATED)
    FetchContent_Populate(wam_llamacpp)
  endif()
  set(_wam_llamacpp_dir ${wam_llamacpp_SOURCE_DIR})
endif()
# ggml is built from a copy in the build tree with third_party/ggml/metal-sparse-mla.patch applied (ggml_sparse_mla:
# the DSA layers' sparse attention as one Metal kernel; the checkout stays untouched).  The copy is redone when the
# checkout, its ggml.h or the patch changes.  A checkout the patch does not apply to (or no git) builds as it is, and the
# engine keeps its gather + mul_mm attention.
set(_wam_gg ${_wam_llamacpp_dir}/ggml)
set(_wam_mla_patch ${CMAKE_CURRENT_SOURCE_DIR}/third_party/ggml/metal-sparse-mla.patch)
set(_wam_alloc_patch ${CMAKE_CURRENT_SOURCE_DIR}/third_party/ggml/metal-allocator-free-blocks.patch)
set(_wam_mla_copy ${CMAKE_BINARY_DIR}/wam-ggml)
file(SHA256 ${_wam_mla_patch} _wam_mla_patch_sha)
file(SHA256 ${_wam_alloc_patch} _wam_alloc_patch_sha)
file(SHA256 ${_wam_gg}/include/ggml.h _wam_mla_src_sha)
set(_wam_mla_stamp "${_wam_gg}|${_wam_mla_src_sha}|${_wam_mla_patch_sha}|${_wam_alloc_patch_sha}")
set(_wam_mla_done "")
if(EXISTS ${_wam_mla_copy}/.wam-stamp)
  file(READ ${_wam_mla_copy}/.wam-stamp _wam_mla_done)
endif()
set(_wam_mla ON)
if(NOT _wam_mla_done STREQUAL _wam_mla_stamp)
  find_package(Git)
  if(NOT GIT_FOUND)
    message(WARNING "git was not found to apply third_party/ggml/metal-sparse-mla.patch: ggml is built as it is, and the "
                    "DSA layers keep the gather + mul_mm attention")
    set(_wam_mla OFF)
  else()
    file(REMOVE_RECURSE ${_wam_mla_copy})
    file(COPY ${_wam_gg}/ DESTINATION ${_wam_mla_copy})
    # (GIT_CEILING_DIRECTORIES: git applies the patch as a plain patch tool, not inside the repository holding the
    # build tree)
    execute_process(COMMAND ${CMAKE_COMMAND} -E env GIT_CEILING_DIRECTORIES=${CMAKE_BINARY_DIR}
                            ${GIT_EXECUTABLE} apply --ignore-whitespace ${_wam_mla_patch}
                    WORKING_DIRECTORY ${_wam_mla_copy} RESULT_VARIABLE _wam_mla_rc ERROR_VARIABLE _wam_mla_err)
    if(_wam_mla_rc EQUAL 0)
      # The pinned ggml allocator's 256 free ranges can fragment during long
      # Metal prefill and abort at ~92K input tokens. Patch ONLY the copied ggml
      # tree, never the vendored llama.cpp checkout. Fail closed on patch drift.
      execute_process(COMMAND ${CMAKE_COMMAND} -E env GIT_CEILING_DIRECTORIES=${CMAKE_BINARY_DIR}
                              ${GIT_EXECUTABLE} apply --ignore-whitespace ${_wam_alloc_patch}
                      WORKING_DIRECTORY ${_wam_mla_copy} RESULT_VARIABLE _wam_alloc_rc
                      ERROR_VARIABLE _wam_alloc_err)
      if(NOT _wam_alloc_rc EQUAL 0)
        message(FATAL_ERROR "Metal ggml allocator patch failed against the pinned ggml copy:\n${_wam_alloc_err}")
      endif()
      file(WRITE ${_wam_mla_copy}/.wam-stamp "${_wam_mla_stamp}")
    else()
      message(WARNING "third_party/ggml/metal-sparse-mla.patch (made for the pinned ggml) does not apply to ${_wam_gg} - "
                      "it is built as it is, and the DSA layers keep the gather + mul_mm attention:\n${_wam_mla_err}")
      file(REMOVE_RECURSE ${_wam_mla_copy})
      set(_wam_mla OFF)
    endif()
  endif()
endif()
set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS ${_wam_mla_patch} ${_wam_alloc_patch} ${_wam_gg}/include/ggml.h)
if(_wam_mla)
  add_subdirectory(${_wam_mla_copy} ${CMAKE_BINARY_DIR}/ggml EXCLUDE_FROM_ALL)
else()
  add_subdirectory(${_wam_gg} ${CMAKE_BINARY_DIR}/ggml EXCLUDE_FROM_ALL)
endif()

# the engine reports Wam's release (VERSION) as its version
file(STRINGS ${CMAKE_CURRENT_SOURCE_DIR}/VERSION _wam_version LIMIT_COUNT 1)
add_library(wam_metal STATIC src/metal/glm_metal.cpp src/metal/glm_metal_load.cpp src/metal/expert_pool.cpp
                              src/metal/glm_metal_dflash.cpp src/metal/spec.cpp src/metal/sampler.cpp)
target_include_directories(wam_metal PUBLIC src/metal)
target_compile_definitions(wam_metal PUBLIC WAM_VERSION="${_wam_version}")
target_link_libraries(wam_metal PUBLIC ggml ggml-base ggml-cpu ggml-metal)
if(_wam_mla)
  target_compile_definitions(wam_metal PRIVATE WAM_METAL_SPARSE_MLA=1)
endif()

add_executable(wam-metal src/metal/main.cpp src/metal/serve.cpp)
target_link_libraries(wam-metal PRIVATE wam_metal)

if(WAM_METAL_TESTS)
  enable_testing()
  if(_wam_mla)
    # Pure allocator regression: no model weights or Metal device required.
    add_executable(metal_allocator_free_blocks_test tools/test_metal_alloc_free_blocks.c)
    target_include_directories(metal_allocator_free_blocks_test PRIVATE
      ${_wam_mla_copy}/include ${_wam_mla_copy}/src)
    target_compile_options(metal_allocator_free_blocks_test PRIVATE -ffunction-sections -fdata-sections)
    target_link_options(metal_allocator_free_blocks_test PRIVATE -Wl,-dead_strip)
    add_test(NAME metal_allocator_free_blocks COMMAND metal_allocator_free_blocks_test)
  endif()
  # the CUDA engine's GLM gates (src/core/glm_model_test.cpp, glm_model_big_test.cpp) on wam-metal
  add_executable(metal_model_test src/metal/metal_model_test.cpp)
  target_link_libraries(metal_model_test PRIVATE wam_metal)
  add_executable(metal_sampler_test src/metal/metal_sampler_test.cpp)
  target_link_libraries(metal_sampler_test PRIVATE wam_metal)
  add_test(NAME metal_sampler_test COMMAND metal_sampler_test)
  # the --serve protocol as serve/server.py drives it: PAUSE keeps the state, seeded sampling repeats, spec=1 changes
  # nothing (tools/test_metal_serve.py; Python without numpy)
  find_program(WAM_SERVE_PYTHON3 python3)
  if(WAM_SERVE_PYTHON3)
    add_test(NAME metal_serve_test
             COMMAND ${WAM_SERVE_PYTHON3} ${CMAKE_CURRENT_SOURCE_DIR}/tools/test_metal_serve.py $<TARGET_FILE:wam-metal>
                     ${CMAKE_CURRENT_SOURCE_DIR}/data/glm5-synth.gguf)
  endif()
  add_test(NAME metal_model_test COMMAND metal_model_test small)
  # the same gates on ggml-cpu (the oracle backend), and with the expert pool at its floor and small slices
  add_test(NAME metal_model_test_cpu COMMAND metal_model_test small)
  add_test(NAME metal_model_test_evict COMMAND metal_model_test small)
  set_tests_properties(metal_model_test_cpu PROPERTIES ENVIRONMENT WAM_METAL_CPU=1)
  set_tests_properties(metal_model_test_evict PROPERTIES
                       ENVIRONMENT "WAM_METAL_POOL_SLOTS=1;WAM_METAL_UBATCH=3;WAM_METAL_ATTN_CHUNK=2")
  # verify windows (speculative decoding): partly wrong drafts must decode exactly as plain decoding does
  add_test(NAME metal_model_window COMMAND metal_model_test window data/glm5-synth.gguf 40)
  # a 2048-token slice gathered 4 tokens a chunk: the graph outgrows any fixed budget (--ubatch 2048 aborted)
  add_test(NAME metal_model_big_slice COMMAND metal_model_test prefill data/glm5-synth.gguf 2048)
  set_tests_properties(metal_model_big_slice PROPERTIES ENVIRONMENT "WAM_METAL_UBATCH=2048;WAM_METAL_ATTN_CHUNK=4")
  set_tests_properties(metal_model_test metal_model_test_cpu metal_model_test_evict metal_model_window
                       metal_model_big_slice PROPERTIES WORKING_DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR})
  # the 45-layer fixture: its 1.2 GB GGUF is generated into the build tree (tools/glm_big_fixture.py needs numpy)
  find_program(WAM_PYTHON3 python3)
  if(WAM_PYTHON3)
    add_test(NAME metal_model_big_gen
             COMMAND ${WAM_PYTHON3} ${CMAKE_CURRENT_SOURCE_DIR}/tools/glm_big_fixture.py
                     --out ${CMAKE_CURRENT_BINARY_DIR}/glm5-big-fixture.gguf)
    set_tests_properties(metal_model_big_gen PROPERTIES FIXTURES_SETUP metal_big_gguf)
    add_test(NAME metal_model_big_test COMMAND metal_model_test big ${CMAKE_CURRENT_BINARY_DIR}/glm5-big-fixture.gguf)
    add_test(NAME metal_model_big_test_evict
             COMMAND metal_model_test big ${CMAKE_CURRENT_BINARY_DIR}/glm5-big-fixture.gguf)
    set_tests_properties(metal_model_big_test_evict PROPERTIES
                         ENVIRONMENT "WAM_METAL_POOL_SLOTS=1;WAM_METAL_UBATCH=64")
    add_test(NAME metal_model_big_window COMMAND metal_model_test window ${CMAKE_CURRENT_BINARY_DIR}/glm5-big-fixture.gguf 48)
    add_test(NAME metal_model_big_window_evict
             COMMAND metal_model_test window ${CMAKE_CURRENT_BINARY_DIR}/glm5-big-fixture.gguf 48)
    set_tests_properties(metal_model_big_window_evict PROPERTIES ENVIRONMENT "WAM_METAL_POOL_SLOTS=1")
    # MTP drafting (the NextN block, tools/glm_mtp_fixture.py adds one to the fixtures) decodes as plain decoding does
    add_test(NAME metal_mtp_gen
             COMMAND ${WAM_PYTHON3} ${CMAKE_CURRENT_SOURCE_DIR}/tools/glm_mtp_fixture.py
                     --out ${CMAKE_CURRENT_BINARY_DIR}/glm5-mtp-small.gguf)
    add_test(NAME metal_mtp_big_gen
             COMMAND ${WAM_PYTHON3} ${CMAKE_CURRENT_SOURCE_DIR}/tools/glm_mtp_fixture.py --big
                     --out ${CMAKE_CURRENT_BINARY_DIR}/glm5-mtp-big.gguf)
    set_tests_properties(metal_mtp_gen metal_mtp_big_gen PROPERTIES FIXTURES_SETUP metal_mtp_gguf)
    add_test(NAME metal_model_mtp COMMAND metal_model_test mtp ${CMAKE_CURRENT_BINARY_DIR}/glm5-mtp-small.gguf 40)
    add_test(NAME metal_model_big_mtp COMMAND metal_model_test mtp ${CMAKE_CURRENT_BINARY_DIR}/glm5-mtp-big.gguf 48)
    add_test(NAME metal_model_big_mtp_evict COMMAND metal_model_test mtp ${CMAKE_CURRENT_BINARY_DIR}/glm5-mtp-big.gguf 48)
    set_tests_properties(metal_model_big_mtp_evict PROPERTIES ENVIRONMENT "WAM_METAL_POOL_SLOTS=1")
    # DFlash2 drafting (tools/glm_dflash_fixture.py: a drafter for the 45-layer fixture, its KV ring wrapping)
    add_test(NAME metal_dflash_gen
             COMMAND ${WAM_PYTHON3} ${CMAKE_CURRENT_SOURCE_DIR}/tools/glm_dflash_fixture.py
                     --out ${CMAKE_CURRENT_BINARY_DIR}/glm5-dflash-drafter.gguf)
    set_tests_properties(metal_dflash_gen PROPERTIES FIXTURES_SETUP metal_mtp_gguf)
    add_test(NAME metal_model_big_dflash COMMAND metal_model_test dflash ${CMAKE_CURRENT_BINARY_DIR}/glm5-big-fixture.gguf
                                                 ${CMAKE_CURRENT_BINARY_DIR}/glm5-dflash-drafter.gguf 120)
    # quantized experts (Q8_0): a 512-token prompt slice takes ggml-metal's matrix-matrix expert kernel (at most 1024
    # experts a tensor), all resident and with a small pool
    add_test(NAME metal_q8_gen
             COMMAND ${WAM_PYTHON3} ${CMAKE_CURRENT_SOURCE_DIR}/tools/glm_mtp_fixture.py --big --no-nextn --q8-experts
                     --out ${CMAKE_CURRENT_BINARY_DIR}/glm5-big-q8.gguf)
    set_tests_properties(metal_q8_gen PROPERTIES FIXTURES_SETUP metal_mtp_gguf)
    add_test(NAME metal_model_big_prefill COMMAND metal_model_test prefill ${CMAKE_CURRENT_BINARY_DIR}/glm5-big-q8.gguf 512)
    add_test(NAME metal_model_big_prefill_evict
             COMMAND metal_model_test prefill ${CMAKE_CURRENT_BINARY_DIR}/glm5-big-q8.gguf 512)
    set_tests_properties(metal_model_big_prefill_evict PROPERTIES ENVIRONMENT "WAM_METAL_POOL_SLOTS=1")
    # 16 heads: the DSA products take ggml-metal's mul_mm (dsa_mm), the gathers padded and not, tails of 1-4 tokens
    add_test(NAME metal_wide_gen
             COMMAND ${WAM_PYTHON3} ${CMAKE_CURRENT_SOURCE_DIR}/tools/glm_mtp_fixture.py --wide --no-nextn
                     --out ${CMAKE_CURRENT_BINARY_DIR}/glm5-wide.gguf)
    add_test(NAME metal_wide512_gen
             COMMAND ${WAM_PYTHON3} ${CMAKE_CURRENT_SOURCE_DIR}/tools/glm_mtp_fixture.py --wide 512 --no-nextn
                     --out ${CMAKE_CURRENT_BINARY_DIR}/glm5-wide512.gguf)
    set_tests_properties(metal_wide_gen metal_wide512_gen PROPERTIES FIXTURES_SETUP metal_mtp_gguf)
    add_test(NAME metal_model_wide_prefill COMMAND metal_model_test prefill ${CMAKE_CURRENT_BINARY_DIR}/glm5-wide.gguf 300)
    # (the 512-wide latent: ggml_sparse_mla's D=512 kernel, the engine's own latent width)
    add_test(NAME metal_model_wide512_prefill
             COMMAND metal_model_test prefill ${CMAKE_CURRENT_BINARY_DIR}/glm5-wide512.gguf 300)
    # the same with the latent kept F32 (--kv-f32): ggml_sparse_mla's F32 kernel
    add_test(NAME metal_model_wide512_prefill_f32
             COMMAND metal_model_test prefill ${CMAKE_CURRENT_BINARY_DIR}/glm5-wide512.gguf 300)
    set_tests_properties(metal_model_wide512_prefill_f32 PROPERTIES ENVIRONMENT "WAM_METAL_ATTN_CHUNK=5;WAM_METAL_KV_F32=1"
                         WORKING_DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR} FIXTURES_REQUIRED metal_mtp_gguf)
    set_tests_properties(metal_model_wide_prefill metal_model_wide512_prefill PROPERTIES ENVIRONMENT "WAM_METAL_ATTN_CHUNK=5"
                         WORKING_DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR} FIXTURES_REQUIRED metal_mtp_gguf)
    set_tests_properties(metal_model_big_prefill metal_model_big_prefill_evict PROPERTIES
                         WORKING_DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR} FIXTURES_REQUIRED metal_mtp_gguf TIMEOUT 3600)
    set_tests_properties(metal_model_mtp metal_model_big_mtp metal_model_big_mtp_evict PROPERTIES
                         WORKING_DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR} FIXTURES_REQUIRED metal_mtp_gguf TIMEOUT 3600)
    set_tests_properties(metal_model_big_dflash PROPERTIES WORKING_DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR}
                         FIXTURES_REQUIRED "metal_big_gguf;metal_mtp_gguf" TIMEOUT 3600)
    set_tests_properties(metal_model_big_test metal_model_big_test_evict metal_model_big_window metal_model_big_window_evict PROPERTIES
                         WORKING_DIRECTORY ${CMAKE_CURRENT_SOURCE_DIR} FIXTURES_REQUIRED metal_big_gguf TIMEOUT 3600)
  endif()
endif()

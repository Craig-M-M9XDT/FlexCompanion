cmake_minimum_required(VERSION 3.18)
project(flexcore LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
if(NOT CMAKE_BUILD_TYPE)
  set(CMAKE_BUILD_TYPE Release)
endif()

find_package(Python 3.9 REQUIRED COMPONENTS Interpreter Development.Module)
find_package(pybind11 CONFIG REQUIRED)

pybind11_add_module(flexcore src/core/flexcore.cpp)
if(CMAKE_CXX_COMPILER_ID MATCHES "GNU|Clang")
  target_compile_options(flexcore PRIVATE -O3 -Wall -Wextra)
endif()

install(TARGETS flexcore DESTINATION flexcompanion)

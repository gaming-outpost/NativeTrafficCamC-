#!/bin/bash
export LD_LIBRARY_PATH="/usr/lib:$LD_LIBRARY_PATH"

if [[ "${CCC_ENABLE_LD_DEBUG_LIBS:-0}" == "1" ]]; then
  export LD_DEBUG=libs
fi

cd "$(dirname "$0")"
dotnet run "$@"

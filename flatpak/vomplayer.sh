#!/bin/bash
# Trampoline from /app/bin/vomplayer to the .NET apphost shipped under /app/lib/vomplayer.
# .NET resolves managed assemblies and bundled native libraries (libhdr_helper.so) relative
# to the apphost's directory, so we exec it directly rather than cd-ing first.
exec /app/lib/vomplayer/Vomplayer "$@"

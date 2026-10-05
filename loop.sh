#!/bin/bash
# запуск сервера с автоперезапуском: выход с кодом 42 = рестарт (кнопка из админки)
cd "$(dirname "$0")"
export DOTNET_ROOT=/root/.dotnet PATH=/root/.dotnet:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1
while true; do
  dotnet IndusBrawl.Laser.Server.dll
  code=$?
  echo "server exited with code $code"
  if [ "$code" -eq 42 ]; then
    echo "restart requested, waiting 3s..."
    sleep 3
    continue
  fi
  break
done

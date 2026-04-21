docker build . -t test
docker run -p 8095:8095/udp test

# Optional local loopback self-test
docker run -e TOUHOU99RELAY_ENABLE_SELF_TEST=1 -p 8095:8095/udp test

# On Apple Silicon, prefer Docker/Compose with linux/amd64 because the bundled
# GameNetworkingSockets native library is x64-only.


docker run --rm -it mcr.microsoft.com/dotnet/runtime:9.0 bash
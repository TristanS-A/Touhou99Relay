docker build . -t test
docker run test -p 8095:8095


docker run --rm -it mcr.microsoft.com/dotnet/runtime:9.0 bash
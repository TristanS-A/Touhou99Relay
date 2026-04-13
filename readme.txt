docker build . -t test
docker run test -p 50295:50295


docker run --rm -it mcr.microsoft.com/dotnet/runtime:9.0 bash
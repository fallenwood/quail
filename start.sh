pushd .
cd ./src/quail.client/
bun run build
popd
dotnet run --project ./src/quail/

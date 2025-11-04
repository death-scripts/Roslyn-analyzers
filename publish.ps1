Remove-Item -Recurse -Force .\out

dotnet clean DeathScriptsAnalyzer/DeathScriptsAnalyzer.csproj

dotnet pack DeathScriptsAnalyzer/DeathScriptsAnalyzer.csproj -c Release -o out

dotnet nuget push "out\*.nupkg" `
  --source "https://nuget.pkg.github.com/JohnSchruben/index.json" `
  --api-key $env:GITHUB_TOKEN `
  --configfile .\NuGet.config `

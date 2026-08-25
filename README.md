# NuExtVault

[![CI](https://github.com/Nigusu-Allehu/NuExtVault/actions/workflows/ci.yml/badge.svg)](https://github.com/Nigusu-Allehu/NuExtVault/actions/workflows/ci.yml)
[![Documentation examples](https://github.com/Nigusu-Allehu/NuExtVault/actions/workflows/documentation.yml/badge.svg)](https://github.com/Nigusu-Allehu/NuExtVault/actions/workflows/documentation.yml)

NuExtVault is a local NuGet-compatible server for integration tests and
development workflows.

## Quick start

Install the .NET SDK 10.0, then install and run the global tool:

```powershell
dotnet tool install --global NuExtVault
nuextvault start
```

Contributors can run the source checkout with
`dotnet run --project .\src\NuExtVault.Cli -- start`.

- [User manual](docs/user/README.md)
- [Contributor manual](docs/contributing/README.md)

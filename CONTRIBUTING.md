# Contributing

Contributions are welcome. Open an issue to discuss a bug or proposed change,
or submit a pull request with a focused implementation.

## Prerequisites

Install Git and a stable .NET SDK compatible with [`global.json`](global.json).
Download the SDK from the
[.NET download page](https://dotnet.microsoft.com/download).
[`global.json`](global.json) uses `latestFeature` to select the highest
installed compatible feature band and patch.

## Build and test locally

From the repository root, run the same commands used by CI (CI sets `src` as
the working directory). Restore requires access to NuGet.org:

```console
cd src
dotnet restore
dotnet build -c Release --no-restore
dotnet test --no-restore -v normal -c Release --results-directory test-results -l trx
```

To create the same package artifacts validated by CI, continue from `src`:

```console
dotnet pack -c Release --no-build --output package-output Valleysoft.DockerCredsProvider
```

## Submit a pull request

Before opening a pull request:

1. Add or update tests for behavior changes.
2. Update `README.md` when a change affects installation, configuration, the
   public API, or error behavior.
3. Run the Release build and test commands above.
4. Keep the pull request focused on one change and explain its user-visible
   effect.

CI builds and tests pull requests on Linux and Windows, and validates package
contents on Linux.

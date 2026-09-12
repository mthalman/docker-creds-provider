# Contributing

Contributions are welcome. Open an issue to discuss a bug or proposed change,
or submit a pull request with a focused implementation.

## Prerequisites

Install the .NET SDK version selected by [`global.json`](global.json). Run
`dotnet --version` from the repository root to confirm that a compatible SDK
is installed. Download .NET SDKs from the
[.NET download page](https://dotnet.microsoft.com/download).

Install the .NET 8 runtime as well; the SDK selected by `global.json` does not
include that runtime. Use `dotnet --list-runtimes` to check that version 8 of
`Microsoft.NETCore.App` is available.

Docker is not required. Unit tests use isolated configuration and process mocks.
Integration tests build and run the repository's `docker-credential-test` helper
executable, without accessing installed Docker helpers, keychains, or registries.

## Build and test locally

From the repository root, run the same commands used by CI (CI sets `src` as the working directory):

    cd src
    dotnet restore
    dotnet build -c Release --no-restore
    dotnet test --no-restore -v normal -c Release --results-directory test-results -l trx

A successful test run reports no failed tests.

The test project and helper target `net8.0`, the lowest supported modern runtime.
To run one suite from `src`, use:

    dotnet test Valleysoft.DockerCredsProvider.Test --no-restore -c Release --filter FullyQualifiedName~CredsProviderIntegrationTests

Use these class names to select other suites:

| Suite | Coverage |
|---|---|
| `CredsProviderTests` | Public entry-point contracts |
| `ConfigDiscoveryTests` | Configuration paths, precedence, and fallback |
| `RegistryMatchingTests` | Docker and containers registry compatibility |
| `EncodedStoreTests` | Inline credentials and malformed encoded data |
| `NativeStoreTests` | Helper selection and response/error contracts |
| `NativeStoreIntegrationTests` | Real helper responses and stream failures |
| `CredsProviderIntegrationTests` | Config-to-helper protocol and concurrent lookups |
| `ProcessServiceTests` | Output byte limits, cancellation, deadlines, and cleanup |

Process tests use short test-specific deadlines where possible while verifying
that native credential lookup retains its 30-second default. Integration tests
own their temporary files and helper processes and clean them up on failure.
Do not point fixture tests at your real Docker configuration.

To create the same package artifacts validated by CI, continue with:

    dotnet pack -c Release --no-build --output package-output Valleysoft.DockerCredsProvider

Packing validates the public API against the latest stable compatibility
baseline. The `package-output` directory will contain both a `.nupkg` and its
matching `.snupkg` symbol package.

## Submit a pull request

Before opening a pull request:

1. Add or update tests for behavior changes.
2. Update `README.md` when a change affects installation, configuration, the
   public API, or error behavior.
3. Run the Release build and test commands above.
4. Keep the pull request focused on one change and explain its user-visible
   effect.

CI runs .NET 8 tests, including real helper integration tests, on Ubuntu, Windows,
and macOS. The library continues to build and undergo package validation for
`netstandard2.0`, `net8.0`, and `net9.0`. CI uploads separate test results for
each operating system.

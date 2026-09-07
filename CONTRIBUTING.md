# Contributing

Contributions are welcome. Open an issue to discuss a bug or proposed change,
or submit a pull request with a focused implementation.

## Prerequisites

Install the .NET SDK version selected by [`global.json`](global.json). Run
`dotnet --version` from the repository root to confirm that a compatible SDK
is installed. Download .NET SDKs from the
[.NET download page](https://dotnet.microsoft.com/download).

Docker is not required to run the automated tests because the test suite mocks
configuration files and credential helper processes.

## Build and test locally

From the repository root, run the same commands used by CI (CI sets `src` as the working directory):

    cd src
    dotnet restore
    dotnet build -c Release --no-restore
    dotnet test --no-restore -v normal -c Release --results-directory test-results -l trx

A successful test run reports no failed tests.

## Submit a pull request

Before opening a pull request:

1. Add or update tests for behavior changes.
2. Update `README.md` when a change affects installation, configuration, the
   public API, or error behavior.
3. Run the Release build and test commands above.
4. Keep the pull request focused on one change and explain its user-visible
   effect.

CI builds and tests pull requests on Linux and Windows.

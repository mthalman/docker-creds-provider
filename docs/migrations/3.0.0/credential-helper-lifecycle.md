# Credential helpers enforce timeouts, output limits, and UTF-8 encoding

**Version introduced:** 3.0.0

Review this guide if your credential helper takes 30 seconds or longer, writes
more than 1 MiB to either output stream, or uses an encoding other than UTF-8.
The `netstandard2.0` asset also checks the host's input encoding.
Inline credentials are unaffected.

**Version note:** Output limits and encoding checks are new in 3.0.0. The
30-second timeout already applies in 2.3.0; the timeout guidance is for upgrades
from 2.2.4 or earlier.

## Previous behavior

Helper standard output and standard error were accumulated without a size
limit, using the runtime's default redirected-stream encodings. The library did
not check whether the host input encoding produced UTF-8-compatible bytes before
starting the helper.

Before 2.3.0, the library waited indefinitely for helpers to exit.
Version 2.3.0 introduced the 30-second timeout.

## New behavior

### Timeout

Each helper call has a fixed **30-second timeout**. A timeout throws
`TimeoutException`, not `CredsNotFoundException`. Both overloads enforce it,
even with `CancellationToken.None`.

### Output limits

Each helper invocation limits standard output and standard error independently
to **1 MiB (1,048,576 raw bytes) per stream**. Exactly that many bytes are
permitted; exceeding either limit throws `InvalidOperationException`, even if
the helper would otherwise return valid credentials. The limit includes
multibyte UTF-8 sequences, any byte-order mark, and newline bytes. It is not a
character count or a combined allowance across both streams.

The output limits are not publicly configurable.

### Encoding

Standard output and standard error are decoded as UTF-8. Modern .NET targets
write standard input as UTF-8 without a byte-order mark. The `netstandard2.0`
build instead checks that the host input encoding emits no byte-order mark and
produces the same bytes as UTF-8 for the actual input and newline. If it cannot
do so, the call fails with a contextual `InvalidOperationException` before
starting the helper. The library does not change process-wide console encoding.

## Type of breaking change

**Behavioral change.** Helpers that previously succeeded with oversized output or
runtime-default encodings can now fail or return differently decoded values.
The .NET Standard input-encoding check can reject a call that previously started
the helper. Upgrades from before 2.3.0 also introduce `TimeoutException` for
helpers that take too long. The timeout is unchanged from 2.3.0.

## Reason for change

Unbounded helper output could consume excessive memory. Explicit UTF-8 encoding
makes the helper protocol consistent across supported runtimes, and checking
legacy host input encoding prevents sending incompatible bytes to the helper.
The existing timeout prevents a stalled helper from blocking credential
retrieval indefinitely.

## Recommended action

Test a lookup with disposable credentials on each deployed runtime. Check the
returned values without logging secrets. If the helper meets these requirements,
no changes are needed.

### Slow or interactive helpers

Update or configure the helper to finish within 30 seconds. Complete any login
or credential-store unlock before calling the library. Investigate slow network
requests or helper startup work; increasing your cancellation deadline will not
help.

Catch `TimeoutException` around the awaited lookup if you need to report or
recover from timeouts. To verify this handling, use an isolated test helper that
stays running for longer than 30 seconds. Confirm that lookup throws
`TimeoutException`, even with `CancellationToken.None`. Allow extra time for
process cleanup.

### Oversized output

If a helper exceeds the output limit, update or configure it to reduce its
response or diagnostics. For custom helpers, keep each stream within 1 MiB for
the entire invocation, including newline bytes. Write only the credential
response to standard output and never include secrets in diagnostics. There is
no library setting to increase the limit. To verify a custom helper's boundary
handling, check that 1,048,576 bytes on standard error are permitted and
1,048,577 bytes fail even when standard output contains valid credential JSON.

### Encoding problems

If a helper relies on another encoding, update or configure it to consume the
newline-terminated UTF-8 server key and emit UTF-8 responses. Verify non-ASCII
values round-trip correctly rather than relying on ASCII-only tests.

If the `netstandard2.0` asset rejects the host input encoding, the setting to
check is `Console.InputEncoding`. Changing it affects console input throughout
the process, not just credential helpers. If your application controls that
setting and can safely use UTF-8, set it once at startup, before credential
lookup or other console input:

```csharp
Console.InputEncoding = new System.Text.UTF8Encoding(
    encoderShouldEmitUTF8Identifier: false);
```

Do not make this process-wide change unless the encoding issue affects your
application. Verify the previously failing lookup and any other console-input
consumers on the actual host.

Alternatively, retarget your application to .NET 8 or later and restore packages
so it uses this package's `net8.0` asset, which sets helper input encoding
directly. Installing a newer runtime alone does not replace the `netstandard2.0`
asset selected by your application's build.

## Affected APIs

Both public overloads in `Valleysoft.DockerCredsProvider` are affected when they
invoke a native credential helper:

- `CredsProvider.GetCredentialsAsync(string registry)`
- `CredsProvider.GetCredentialsAsync(string registry, CancellationToken cancellationToken)`

Inline credentials in `auths` do not invoke a helper and are not subject to
the helper timeout, output limits, or encoding requirements.

## References

- [Cancellation and helper timeout introduced in 2.3.0 (#92)](https://github.com/mthalman/docker-creds-provider/pull/92)
- [Credential-helper process lifecycle changes (#97)](https://github.com/mthalman/docker-creds-provider/pull/97)
- [Follow-up correction for the helper stdin closure race (#102)](https://github.com/mthalman/docker-creds-provider/pull/102)
- [Docker credential helper protocol](https://docs.docker.com/reference/cli/docker/login/#credential-helper-protocol)

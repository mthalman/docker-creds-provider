# Credential helpers enforce timeouts, output limits, and UTF-8 encoding

**Version introduced:** 3.0.0

This guide covers native credential helpers that take too long to complete, emit
more than 1 MiB on either output stream, or rely on a non-UTF-8 encoding.
Applications using the `netstandard2.0` asset can also be affected by host input-encoding
compatibility checks. Compatible helpers generally require no application-code
changes. Inline credentials are unaffected.

The output limits and encoding changes were introduced in 3.0.0. The fixed
30-second helper timeout was introduced in 2.3.0 and remains in effect; its
migration guidance also applies when upgrading from 2.2.4 or earlier to 3.0.0.

## Previous behavior

Helper standard output and standard error were accumulated without a size
limit, using the runtime's default redirected-stream encodings. The library did
not check whether the host input encoding produced UTF-8-compatible bytes before
starting the helper.

Before 2.3.0, helper execution waited indefinitely for the process to exit.
A slow or interactive helper could eventually succeed after more than 30
seconds, while a hung helper could leave credential retrieval blocked.
Version 2.3.0 already enforced the 30-second timeout.

## New behavior

Each helper invocation has a fixed **30-second timeout**. If the helper does not
complete within that interval, credential retrieval throws `TimeoutException`;
it does not return credentials or treat the timeout as missing credentials.
This applies to both public overloads, including calls without a cancellation
token and calls with `CancellationToken.None`. A caller's longer cancellation
deadline does not extend the helper timeout. The timeout is not publicly
configurable.

Each helper invocation limits standard output and standard error independently
to **1 MiB (1,048,576 raw bytes) per stream**. Exactly that many bytes are
permitted; exceeding either limit throws `InvalidOperationException`, even if
the helper would otherwise return valid credentials. The limit includes
multibyte UTF-8 sequences, any byte-order mark, and newline bytes. It is not a
character count or a combined allowance across both streams.

Standard output and standard error are decoded as UTF-8. Modern .NET targets
write standard input as UTF-8 without a byte-order mark. The `netstandard2.0`
build instead checks that the host input encoding emits no byte-order mark and
produces the same bytes as UTF-8 for the actual input and newline. If it cannot
do so, the call fails with a contextual `InvalidOperationException` before
starting the helper. The library does not change process-wide console encoding.

The output limits are not publicly configurable.

## Type of breaking change

**Behavioral change.** Helpers that previously succeeded with oversized output or
runtime-default encodings can now fail or return differently decoded values.
The .NET Standard input-encoding check can reject a call that previously started
the helper. For callers upgrading from before 2.3.0, a helper that previously
succeeded after more than 30 seconds can now throw `TimeoutException`.
The 30-second limit is unchanged for callers upgrading from 2.3.0.
Public method signatures and supported target frameworks are unchanged in 3.0.0.

## Reason for change

Unbounded helper output could consume excessive memory. Explicit UTF-8 encoding
makes the helper protocol consistent across supported runtimes, and checking
legacy host input encoding prevents sending incompatible bytes to the helper.
The existing timeout prevents a stalled helper from blocking credential
retrieval indefinitely.

## Recommended action

For a compatible helper, keep your existing credential lookup and exception
handling. Verify a lookup on each deployed runtime with disposable credentials,
including non-ASCII credential values if you use them, and check the returned
values without logging secrets.

If your helper can take 30 seconds or longer, update or configure it to complete
within the timeout. Complete any interactive login or credential-store unlock
through the helper's supported procedure before calling the library. Check slow
network requests and custom helper startup work rather than increasing the
caller's cancellation deadline; no library setting disables or increases the
timeout.

Handle `TimeoutException` around the awaited lookup if your application needs
to report or recover from a timed-out helper. Do not rely solely on
`CredsNotFoundException` handling. Verify a successful lookup within 30 seconds
on each deployed host. In an isolated test configuration with a disposable
helper that stays running without completing its response for longer than 30
seconds, verify that the lookup throws `TimeoutException`. Allow time for
process cleanup rather than requiring the exception at exactly 30 seconds.
Passing `CancellationToken.None` must not disable this timeout.

If a helper exceeds the output limit, update or configure it to reduce its
response or diagnostics. For custom helpers, keep each stream within 1 MiB for
the entire invocation, including newline bytes. Write only the credential
response to standard output and never include secrets in diagnostics. There is
no library setting to increase the limit. To verify a custom helper's boundary
handling, check that 1,048,576 bytes on standard error are permitted and
1,048,577 bytes fail even when standard output contains valid credential JSON.

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

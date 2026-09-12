### Credential-helper failures use sanitized exceptions

This change affects applications that catch `JsonException` for native
credential-helper responses or inspect exception messages for helper output.

#### Previous behavior

When a native credential helper returned malformed JSON,
`CredsProvider.GetCredentialsAsync` threw a top-level
`System.Text.Json.JsonException` with the original parser message.

For missing response fields, exception messages included the helper's standard
output. For nonzero helper exits, `CredsNotFoundException` messages included
standard error, or standard output if standard error was empty.

#### New behavior

Malformed helper JSON throws `InvalidOperationException`. Its `InnerException`
is a newly constructed, sanitized `System.Text.Json.JsonException` that retains
the parser's `Path`, `LineNumber`, and `BytePositionInLine` properties, but not
the original parser message. These metadata properties can be null.

Helper failure diagnostics no longer expose raw standard output or standard
error. Nonzero helper exits still throw `CredsNotFoundException`; invalid helper
response fields and non-object JSON responses throw `InvalidOperationException`.
Configuration-file errors retain their existing exception behavior.

#### Type of breaking change

**Behavioral change.** Existing helper-response `catch (JsonException)` handlers
no longer catch malformed helper JSON. Code that relies on raw helper output in
exception messages also needs to change. This public error-contract change
requires a major release even though it does not remove an API.

#### Reason for change

Credential-helper output can contain passwords or tokens. Wrapping parsing
failures with sanitized exceptions prevents helper output from leaking through
diagnostics while retaining parser-location metadata for troubleshooting.

#### Recommended action

Update helper-response exception handling to catch `InvalidOperationException`
with a `JsonException` inner exception. The following catch clauses show the
change around an existing credential lookup.

##### Before

```csharp
catch (System.Text.Json.JsonException)
{
    Console.Error.WriteLine("The credential helper returned malformed JSON.");
}
```

##### After

```csharp
catch (InvalidOperationException ex)
    when (ex.InnerException is System.Text.Json.JsonException jsonError)
{
    Console.Error.WriteLine(
        $"The credential helper returned malformed JSON " +
        $"at line {jsonError.LineNumber}, byte position {jsonError.BytePositionInLine}.");
}
```

Keep any existing `JsonException` handler for configuration-file errors.
Do not replace it with an unfiltered `catch (InvalidOperationException)`, which
would also catch unrelated configuration and helper failures.

Use exception types for control flow and the inner `JsonException` properties
for parser locations. For troubleshooting, helper failure messages include
available context such as the helper name, failure category, captured-output
lengths, or exit code. That message text is diagnostic, not a stable
machine-readable API. Do not attempt to recover raw helper output from the
exception chain.

Verify the migrated handler with a test helper that returns malformed JSON and
exits successfully: expect `InvalidOperationException` with a sanitized
`JsonException` inner exception, not a top-level `JsonException`. Also verify
that configuration-file error handling still works.

#### Affected APIs

Both public overloads in `Valleysoft.DockerCredsProvider` are affected when they
invoke a native credential helper:

- `CredsProvider.GetCredentialsAsync(string registry)`
- `CredsProvider.GetCredentialsAsync(string registry, CancellationToken cancellationToken)`

#### References

- [Credential-helper diagnostic and exception changes (#93)](https://github.com/mthalman/docker-creds-provider/pull/93)

### Credential-helper errors in 3.0.0

#### What changed

**3.0.0 is a major release because
[#93](https://github.com/mthalman/docker-creds-provider/pull/93) changes the public
exception contract.** When a native credential helper returns malformed JSON,
`CredsProvider.GetCredentialsAsync` now throws `InvalidOperationException`
instead of a top-level `System.Text.Json.JsonException`.

Helper failure diagnostics no longer expose raw standard output or standard
error because they may contain passwords or tokens. Nonzero helper exits still
throw `CredsNotFoundException`; invalid helper response fields and non-object JSON
responses throw `InvalidOperationException`.

#### How to migrate

Update code that catches `JsonException` for helper responses to catch
`InvalidOperationException` with an exception filter such as
`when (ex.InnerException is System.Text.Json.JsonException)`. Its `InnerException`
is a sanitized `JsonException` that preserves the parser's `Path`, `LineNumber`,
and `BytePositionInLine` properties (which can be null), but not the original
parser message. Keep existing `JsonException` handling for configuration-file
errors; this change is specific to credential-helper responses.

Use the exception type and available helper name, failure category,
captured-output lengths, exit code, or parser location instead of parsing helper
output from exception messages.

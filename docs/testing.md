# Testing a format

A project's test cases pin down how values and bytes of its types look: a value with its known encoding, bytes from a
capture, input that must be rejected. `format-studio test` runs them against the simulator, the web UI lists and runs
them on the overview, and CI can read the results as JUnit XML.

## Test files

The project file names the test files with globs (the files of a new project's `tests` folder):

```yaml
tests:
  - tests/**/*.yaml
```

Test files are never read as definitions, also when a source pattern such as the default `**/*.yaml` takes them in.
`samples` is the old name of `tests` and is still read; the settings page writes `tests`.

A test file holds a list of cases, a suite or a single case. A suite is a mapping with a `cases` list; its `type`,
`tags` and `skip` apply to all its cases, and `suite` names it (the file name without `.yaml` otherwise).

```yaml
suite: handshake
type: Handshake            # cases without a type of their own have this one
tags: [handshaking]        # added to the tags of every case
cases:
  - name: status
    value: {protocolVersion: 765, serverAddress: localhost, serverPort: 25565, nextState: status}
    hex: fd05 09 6c6f63616c686f7374 63dd 01
  - name: login-capture
    file: fixtures/handshake-login.bin
    expect:
      value: {serverAddress: localhost, nextState: login}
      match: subset
```

Older files with `samples:` lists and `sample:` names still load. Problems with a case (unknown type or key, invalid
hex, a missing file, expectations it can't meet) are reported when the project loads, at the case's line, with the
other diagnostics.

## Cases

| Key | Meaning |
| --- | --- |
| `name` | The case's name; unique in its file (`sample` is the old name) |
| `type` | A type expression, e.g. `Handshake`, `Slot[]` or `zlib(Slot)`; the suite's type when left out |
| `value` | A value as JSON/YAML, as on the Simulate page |
| `hex` | The expected bytes in hex; spaces, `0x`, `:` and `-` are ignored |
| `file` | A file with the expected bytes instead of `hex`, relative to the test file |
| `expect` | More checks, or a failure the case must end in (below) |
| `skip` | `true` or a reason: the case doesn't run, and its type isn't checked (it may not exist yet) |
| `tags` | Tags for `test --tag` |

Without `expect`, a case checks what samples always did:

- with a `value`, the value must encode, and to the case's bytes when it has `hex` or `file`;
- the bytes (the encoded value, else the case's bytes) must decode, take up all bytes and encode back to themselves.

## Expectations

| Key | Meaning |
| --- | --- |
| `value` | The value the bytes decode to |
| `match` | `exact` (the default): the decoded value has exactly these fields; `subset`: the fields given must match, others are ignored |
| `warnings` | Texts of the warnings the case must give, and no others; `[]` for none. Without the key warnings don't matter |
| `consumed` | How many bytes the type takes up; the bytes after them may be anything (a capture with more in it) |
| `error` | The case must fail with an error containing this text |
| `path` | The case must fail at this field, e.g. `serverAddress` or `location.x` |
| `fails` | `encode` or `decode`: which step must fail. The default is decode when the case has bytes, else encode |

Integers are compared exactly, other numbers to six significant digits, since a `f32` loses digits. A case with
`error`, `path` or `fails` only checks its error.

```yaml
- name: truncated-address
  hex: fd05 09 6c6f
  expect: {error: remaining, path: serverAddress}
- name: unknown-next-state
  hex: fd05 09 6c6f63616c686f7374 63dd 07
  expect: {warnings: [not a member of NextState]}
- name: too-big
  type: u8
  value: 300
  expect: {fails: encode}
```

## Running tests

```
format-studio test [--filter <glob>] [--tag <tag>] [--type <type>] [--file <glob>] [--format text|json|junit] [--output <file>]
```

- `--filter` matches a case's name or `suite/name` (`*` and `?`), `--file` the test file relative to the project;
  each filter may be given several times, and a case runs when it matches every kind of filter given.
- `--format json` lists every case with its status, file, line and failures (kind, message, path, and for bytes that
  differ the offset and both byte strings). `--format junit` writes one `testsuite` per suite.
- `--output` writes the report to a file and prints the text summary to the console, e.g. for CI:
  `format-studio test --format junit --output test-results.xml`.
- The exit code is 1 when a case fails. `format-studio samples` is the same command.

The cases run against the simulator only. In the web UI, the overview's **Tests** panel runs them and shows each
result; a case's page edits its name, type, value and hex and keeps its other keys.

# Testing a format

A project's test cases pin down how values and bytes of its types look: a value with its known encoding, bytes from a
capture, a whole packet with its id, input that must be rejected, random values that must round-trip. `format-studio test` runs them against the simulator
and, with `--target`, against the generated C# and Java; the web UI's Tests page lists, runs and edits them, and CI can read
the results as JUnit XML.

## Test files

The project file names the test files with globs (the files of a new project's `tests` folder):

```yaml
tests:
  - tests/**/*.yaml
```

Test files are never read as definitions, also when a source pattern such as the default `**/*.yaml` takes them in.
`samples` is the old name of `tests` and is still read; the settings page writes `tests`.

A test file holds a list of cases, a suite or a single case. A suite is a mapping with a `cases` list; its `type`,
`packet`, `tags`, `skip` and `skip-targets` apply to all its cases, and `suite` names it (the file name without `.yaml` otherwise).

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
| `hex` | The expected bytes in hex; spaces, `0x`, `:` and `-` are ignored. `auto` to have them filled in ([below](#updating-expected-results)) |
| `file` | A file with the expected bytes instead of `hex`, relative to the test file |
| `expect` | More checks, or a failure the case must end in (below) |
| `packet` | The bytes start with a packet id ([below](#packet-cases)) |
| `fuzz` | Round-trip random values instead ([below](#fuzz-cases)) |
| `skip` | `true` or a reason: the case doesn't run, and its type isn't checked (it may not exist yet) |
| `skip-targets` | Codegen targets or generators the case doesn't run against ([below](#generated-code)): a list, or names with a reason |
| `tags` | Tags for `test --tag` |

Without `expect`, a case checks what samples always did:

- with a `value`, the value must encode, and to the case's bytes when it has `hex` or `file`;
- the bytes (the encoded value, else the case's bytes) must decode, take up all bytes and encode back to themselves.

## Expectations

| Key | Meaning |
| --- | --- |
| `value` | The value the bytes decode to; `auto` to have it filled in |
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

## Packet cases

With `packet`, a case's bytes are a whole packet as it follows the length prefix on the wire: the packet id, then the
packet. The id picks the packet among the packets of a [protocol](dsl.md#protocols), as `decode-packet` does, so a case needs no
type to decode; with a type, the bytes must be that packet, and a value is encoded with the packet's id in front.

```yaml
suite: packets
packet: {header: varint32, protocol: PlayClientbound}   # for all cases of the suite
cases:
  - name: block-update
    hex: 09 4607632c15b4833f 01                          # id 0x09: BlockUpdate
    expect:
      value: {location: {x: 18357644, z: -20882616, y: 831}, blockState: 1}
  - name: set-slot-empty
    type: SetSlot
    value: {windowId: 0, stateId: 4, slot: 36, item: {}}
    hex: 15 00 04 0024 00
  - name: unknown-id
    hex: 7f 00
    expect: {error: no packet has id 127}
```

| Key | Meaning |
| --- | --- |
| `header` | The integer type of the id, e.g. `varint32` or `u16be`; `packet: varint32` is short for `packet: {header: varint32}` |
| `protocol` | The protocol whose packets the id picks from; all packets without it |
| `group` | Only packets in this group as well |
| `id-mask` | The bits of the header that are the id, e.g. `0x3ff` for Bedrock |

Expectations apply to the whole bytes: `consumed` counts the id too, and a mismatch is at its offset in the packet.

## Fuzz cases

A case with `fuzz` makes up random values, encodes them, decodes the bytes and encodes the result again; both encodings
must be the same. It covers the case's type, or without one all packets (all structs of a project without packets),
as `format-studio roundtrip` does. With `packet` too, the values go through the packet id, which checks that each
packet's id picks it again among the packets of its protocol.

```yaml
suite: fuzz
cases:
  - name: all-packets
    fuzz: {iterations: 50}
  - name: play-ids
    packet: {header: varint32, protocol: PlayClientbound}
    fuzz: true
  - name: slot
    type: Slot
    fuzz: {iterations: 200, seed: 7}
```

`iterations` is the number of values per type (20 by default), `seed` picks them (1 by default): the same seed gives
the same values on every machine, so a failure in CI happens locally as well. A type that fails is reported with how
many of its values failed and the first one, which `format-studio encode <type> --json` repeats. A fuzz case has no
`value`, `hex`, `file` or `expect`.

## Updating expected results

When a format changes on purpose, its cases' expected bytes and values change with it. `format-studio test --update`
writes what the failed cases actually give into their files, then runs them again:

- the hex a value encodes to, when it differs from the case's hex, and in place of `hex: auto`;
- the value the bytes decode to, in place of `expect.value` (for `match: subset` only the fields given) or of
  `value: auto`;
- the warnings the case gives, in place of `expect.warnings`, and the bytes it takes up, in place of `expect.consumed`.

The value is taken as right and the bytes as what follows from it. Only these keys are rewritten, where they stand:
comments and the rest of the file stay as they are, and new hex has a space between the top-level fields (and after
a packet id). A case that fails to encode, decode or round-trip, or must fail and doesn't, is left as it is and listed
with the reason, as is one whose bytes come from a `file`. `auto` is a placeholder for writing a case before knowing
its bytes, e.g. a capture's decoded value; until it is filled in the case fails.

```yaml
- name: login
  value: {protocolVersion: 765, serverAddress: localhost, serverPort: 25565, nextState: login}
  hex: auto                      # becomes: hex: fd05 096c6f63616c686f7374 63dd 02
- name: capture
  file: fixtures/handshake-login.bin
  expect: {value: auto}          # becomes: expect: {value: {protocolVersion: 765, ...}}
```

Check the changed files (e.g. with `git diff`) before committing them: an update accepts whatever the definitions
give now, including a bug. In the web UI, **Accept actual** on a failed case does the same for that case (on the Tests
page, or on its page once its edits are saved), and **Accept all actual** on the Tests page for all of them.

## Generated code

`format-studio test --target csharp` runs the cases once more against the code a codegen target generates: the
project's target of that name, else its first with that generator, else the generator's defaults. The code is compiled
(C# with Roslyn against FormatStudio.Runtime, Java with a JDK: `FORMAT_STUDIO_JDK`, `JAVA_HOME` or the PATH), and in one
run, for Java one JVM, it reads the bytes of each case and writes them back:

- the case's bytes (as many as `consumed` says), else those the simulator encodes its value to, must come back the same;
- a case that must fail to decode must make the generated code throw;
- a packet case's id goes to the generated registry of its protocol (or the one registry of a project without
  protocols), which must read the same packet; with a `group` the simulator picks the packet instead;
- fuzz cases send the simulator's random values (the same seed gives the same ones) through the generated code.

The decoded value and warnings are checked by the simulator only, and so is failing to encode: the generated code only
gets bytes. Cases of types without a generated `Read` of their own, such as `Slot[]` or a `u8`, are skipped with the
reason, as are types from libraries. Results are named `suite/name [target]` and counted per target.

Generated code may be stricter than the simulator: a Java enum can't hold a value that isn't a member, so the generated
Java throws where the simulator only warns. `skip-targets` leaves such a case out for the targets or generators it
names, with the reason shown in the report:

```yaml
- name: unknown-next-state
  hex: fd05 09 6c6f63616c686f7374 63dd 07
  expect: {warnings: [not a member of NextState]}
  skip-targets: {java-stream: Java enums reject values that aren't members}   # or a list: [java-stream]
```

In a unit test project, `ProjectSuite` from FormatStudio.Testing makes each case a test of its own, for the simulator
and each target:

```csharp
static readonly ProjectSuite Suite = ProjectSuite.Get("path/to/project", "csharp", "java-stream");
public static IEnumerable<object[]> Cases => Suite.Cases();

[Theory, MemberData(nameof(Cases))]
public void Case(string id)
{
    var result = Suite.Run(id);
    if (result.Skipped) Assert.Skip(result.Summary);
    Assert.True(result.Passed, result.Describe());
}
```

A target is compiled once, when its first case runs. Without a JDK the Java cases are skipped; code that doesn't
compile fails each of the target's cases with the compiler's errors.

## Running tests

```
format-studio test [--filter <glob>] [--tag <tag>] [--type <type>] [--file <glob>] [--format text|json|junit] [--output <file>] [--update] [--target <target|generator>]
```

- `--filter` matches a case's name or `suite/name` (`*` and `?`), `--file` the test file relative to the project;
  each filter may be given several times, and a case runs when it matches every kind of filter given.
- `--format json` lists every case with its status, file, line and failures (kind, message, path, and for bytes that
  differ the offset and both byte strings). `--format junit` writes one `testsuite` per suite.
- `--update` first accepts what the failed cases (of the filters) give, as above; the report is of the cases after it.
- `--target` (may be given several times) runs the cases against generated code as well, [above](#generated-code).
  Code that doesn't compile, or a Java target without a JDK, is an error of the run.
- `--output` writes the report to a file and prints the text summary to the console, e.g. for CI:
  `format-studio test --format junit --output test-results.xml`.
- The exit code is 1 when a case fails or the project has errors, so CI needs no `validate` or `roundtrip` step
  besides it. `format-studio samples` is the same command.

Without `--target` the cases run against the simulator only. In the web UI, the **Tests** page runs them (against the
simulator) and shows each failure, with bytes that differ side by side; a case's page edits the case and what it
expects, and **Save as test** on the Simulate, Identify and Round trip pages adds a case from what they show (see
[usage](usage.md)).

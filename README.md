# Review Sidekick

A standalone .NET console application that runs a local GitHub Copilot SDK session
to resolve management-plane Azure SDK PR feedback. Nothing needs to be added to
`azure-sdk-for-net`, and there is no hosted agent, scheduled automation, or Actions
workflow.

The application collects PR discussion comments, review bodies, inline comments,
and resolved/outdated review threads with their replies. REST lists, review
threads, and replies are paginated independently. It loads the existing
`mgmt-review-comment-resolution` skill from your SDK checkout rather than keeping
a second copy.

GitHub REST reads use Octokit.NET with automatic pagination. Review threads and
replies use a small typed GraphQL client. Both are projected into typed snapshot
records rather than traversing raw JSON documents. Authentication is obtained
from `gh auth token --hostname <PR-host>` and held only in memory; no token is
printed or written into run state. GitHub.com and GitHub Enterprise Server use
their respective REST/GraphQL endpoints.

## Requirements

- .NET 10 SDK.
- Git and authenticated GitHub CLI (`gh auth status`).
- Copilot access and authentication compatible with the Copilot runtime. The
  pinned `GitHub.Copilot.SDK` package acquires its matching runtime during build;
  authentication errors are reported, not bypassed with another account.
- Azure SDK generation prerequisites: PowerShell 7, Node.js, and the dependencies
  required by your SDK checkout.
- Existing local SDK and specification repository checkouts. The SDK checkout
  must already be on the SDK PR's exact head branch and commit. The program does
  not automatically switch your SDK branch or clone repositories.
- The SDK checkout must contain `.github\skills\mgmt-review-comment-resolution`
  and, for implementation/publication,
  `sdk\tools\Azure.GeneratorAgent\src\Azure.GeneratorAgent.csproj`.

All commands and repository operations execute locally. Copilot inference and
GitHub operations still use network services: this is **not an offline agent**.
Only run it against repositories you are authorized to send to Copilot.

## Build and test

```powershell
dotnet build .\ReviewSidekick.slnx
dotnet test .\ReviewSidekick.slnx
```

The SDK version is pinned to 1.0.28. Typed response inference and permission
decisions are experimental; diagnostic suppressions are limited to those calls. An externally supplied
Copilot executable must be compatible with the pinned SDK; prefer the bundled
runtime over editor-specific shims.

The GitHub REST dependency is pinned to Octokit 14.0.0.

If `nuget.org` fails TLS/network access, do not bypass certificate validation.
An alternative public feed used to validate this project is:

```powershell
dotnet restore .\ReviewSidekick.slnx `
  --source https://pkgs.dev.azure.com/azure-sdk/public/_packaging/azure-sdk-for-net/nuget/v3/index.json
```

After that restore, use `--no-restore` with build, test, and run in that environment.

## Use

Run from this standalone repository, not from inside the SDK checkout.

The executable is named `review-sidekick` (`review-sidekick.exe` on Windows).
The examples below use `dotnet run` so installation on `PATH` is not required.

### 1. Plan (read-only)

```powershell
dotnet run --project .\src\ReviewSidekick -- plan `
  --sdk-pr https://github.com/Azure/azure-sdk-for-net/pull/12345 `
  --sdk-repo C:\src\azure-sdk-for-net `
  --spec-repo C:\src\azure-rest-api-specs
```

Optional arguments:

- `--model ID`: select a model; otherwise the runtime chooses its default.
- `--copilot-path PATH`: use an explicitly selected compatible runtime.
- `--run-dir PATH`: choose a new state directory outside both checkouts.

Planning has only read/search/question tools: no shell, file editing, skill
execution, or GeneratorAgent startup. It supplies the existing resolution skill's
instructions directly to the model. The output maps feedback to proposed changes,
unsupported requests, ambiguities, and validation steps.

The command prints the local run directory, which defaults to
`~\.review-sidekick\runs\<unique-id>`. Existing version-2 runs under the previous
`~\.sdk-review-resolver\runs` directory remain usable by supplying their original
path with `--run-dir`; no state directories are moved automatically.

### 2. Apply locally

```powershell
dotnet run --project .\src\ReviewSidekick -- apply --run-dir C:\path\to\run
```

Review the displayed plan and type `approve` to proceed. Both checkouts must
initially be clean. The agent can prepare a spec branch, modify `client.tsp`,
add justified SDK customizations, regenerate against the exact local service spec
directory, and validate. No commits, pushes, PR creation, or thread actions are
authorized in this phase.

The session loads repository skills and explicitly connects the existing
GeneratorAgent MCP server with only these tools:

- `discover_project`
- `run_code_generation`
- `build_and_classify`
- `classify_errors`
- `run_tests`
- `snapshot_generated`
- `verify_generated_unchanged`

Migration cleanup, broad batch fixes, and commit iteration are not exposed.

### 3. Publish

```powershell
dotnet run --project .\src\ReviewSidekick -- publish --run-dir C:\path\to\run
```

Separately approve the implementation and validation report. The agent checks
current diffs and existing related spec PRs, pushes spec changes and creates/reuses
a draft spec PR, pins the SDK to the pushed full spec SHA, regenerates without a
local-spec override, revalidates, and updates the **existing SDK PR branch**.
SDK-only changes do not create an empty spec PR.

The agent must inspect repository PR templates, preserve existing SDK draft/ready
state, use regular pushes, and stop on rejection or unexpected failure. It proposes
thread replies/resolutions in its output but does not execute them yet.

### 4. Review-thread actions

```powershell
dotnet run --project .\src\ReviewSidekick -- review --run-dir C:\path\to\run
```

Separately approve the exact proposed replies and resolutions. The agent refreshes
threads, avoids duplicate replies, and resolves only verified addressed requests.
Unsupported/ambiguous requests remain open. No source changes or publication are
authorized. Replies use the user's attribution footer.

## Approval and safety model

Every tool execution requests a one-time interactive approval, displaying the
runtime permission details. There is no approve-all or unattended flag. Input
redirection is rejected when an approval or agent question is requested. Ctrl+C
cancels the run and disposes the owned runtime.

The phase tool allowlist **enforces read-only planning**. Apply/publish/review
need shell access, so restrictions on commits, network writes, and thread actions
also depend on agent instructions and your inspection of individual permission
requests. This is **not a filesystem or command sandbox**. Do not approve a shell
command that exceeds the current phase. Repository-origin file hooks and ambient
configuration discovery are disabled; the agent is instructed to read relevant
repository guidelines explicitly.

The program rejects stale PR snapshots (including edited comments), changed
resolution-skill contents, closed/merged PRs, skipped phase prerequisites, and
replay of already-attempted phases. A `ready` result is the agent's report,
**not an independent proof of correctness**: review its concrete command results
and diffs before approving publication.

Typed snapshots use run-state version 2. Runs created with the earlier raw-JSON
format are rejected with an instruction to start a fresh plan; old approvals are
not carried forward across the schema change.

If a phase fails, is cancelled, or partially publishes, inspect the saved session
ID and both repositories before starting a fresh plan. It deliberately does not
replay potentially mutating operations automatically. A fresh plan can identify
existing fixes/spec PRs. Partial publication cannot be rolled back atomically
across repositories.

## Local state

Each run has an exclusively locked, atomically updated `run.json` containing:

- PR metadata, changed files, all collected feedback, and review-thread IDs.
- Repository paths, chosen model/runtime path, and the resolution skill hash.
- Phase start times, Copilot session IDs, structured results, and validation evidence.

Treat this directory as potentially sensitive: it contains review content and
may include code snippets. Store it outside repository checkouts, do not upload
it, and delete specific run directories when no longer needed. Credentials are
not copied into run state. The Copilot runtime also keeps its own session data
according to its configuration.

Exit codes: `0` ready/help, `1` error, `2` blocked, `130` cancelled.

This repository can be published independently. Do not include local run state,
build outputs, credentials, or your SDK/spec checkouts.

# Bendover 

Bendover is an opinionated, self improving coding agent.

It’s an experiment in building an agentic system that can learn and apply a specific coding policy aka judgment when ambiguity shows up, instead of falling back to generic pre training led reasoning that doesn’t align.

_The name bendover is ode to a soca song with a line 🎵[...] take it easy I will do the work, you don't have to [...]🎵_

# The Hypothesis

Teaching an LLM to consistently squeeze out ambiguity will improve output quality more than adding more instructions or examples.

# The Experiment

To build a non-trivial bootstrapped agentic coding system that learns from feedback to build itself. 

- Leverage an LLM to write > 95% of the code
- Start with an existing agentic loop (i.e. Claude, Antigravity, Codex, etc.) 
- Capture real coding runs and score outputs
- Replay those runs to learn a policy<sup>*</sup> when tasks are ambiguous

Some notes:
- Behavior is defined by role targeted practices → "this is how I do X"
- Judgment over syntax → identical decisions, not code
- Real work is the dataset → learning comes from specific examples, not volume
- The initial Bendover scaffolding is intentionally opinionated → my own judgement

<sup>*</sup> The policy it learns must match the one I use internally when ambiguity shows up. That policy is expressed through code. 

From here on, everything in this repo has been written by an agent. 

## Prerequisites

- **.NET 10.0 SDK** (or later)
- **Docker Desktop** (Windows) or **Docker Engine** (Linux/WSL)
  - Ensure Docker is running and accessible.

## Setup & Run

### 1. Build the Solution

```bash
dotnet build
```

### 2. Run Integration Tests

The tests require Docker to be running.

```bash
dotnet test
```

### 3. Run the Server

```bash
dotnet run --project src/Bendover.Presentation.Server
```

### 4. Run the CLI Client

```bash
dotnet run --project src/Bendover.Presentation.CLI
```

---

## WSL Setup Guide

To quickly bootstrap your environment (install .NET 8, check Docker), run the setup script:

```bash
chmod +x setup.sh
./setup.sh
```

### Manual Install (if script fails)

```bash
# Remove previous versions (if any)
sudo apt-get remove -y dotnet* aspnetcore* netstandard*

# Add Microsoft package repository
wget https://packages.microsoft.com/config/ubuntu/$(lsb_release -rs)/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
rm packages-microsoft-prod.deb

# Install .NET SDK
sudo apt-get update
sudo apt-get install -y dotnet-sdk-10.0
```

## Configuration

The agent requires configuration to connect to an LLM provider (OpenAI or Local).

Configuration is managed via `src/Bendover.Presentation.CLI/appsettings.json`.
For local development, create/modify `src/Bendover.Presentation.CLI/appsettings.Development.json` (this file is git-ignored).

Example `appsettings.Development.json` for Local LLM (e.g., LM Studio):
```json
{
  "Agent": {
    "Model": "openai/gpt-oss-20b",
    "Endpoint": "http://127.0.0.1:1234",
    "ApiKey": "sk-dummy"
  }
}
```

The agent supports role-based configuration overrides. You can specify different models for `Lead`, `Architect`, `Engineer`, or `Reviewer`.

```json
{
  "Agent": {
    "Model": "gpt-4o",
    "RoleOverrides": {
      "Engineer": {
        "Model": "gpt-4-turbo"
      }
    }
  }
}
```

### ChatGPT Plus/Pro Subscription (No API Key)

If you want to use your existing ChatGPT Plus/Pro subscription instead of an API key, run the CLI connect flow:

```bash
dotnet run --project src/Bendover.Presentation.CLI -- /connect
```

This will open your browser, prompt you to sign in, and store a refreshable session token in `~/.bendover/chatgpt.json`. The CLI will verify access and then use that token for subsequent runs. Configure your model as usual (for example `gpt-5.3-codex`).

## Practices

Bendover uses a library of "Practices" to guide agent behavior. These are markdown files located in `src/Bendover.Application/.bendover/practices/`.

Each practice must include YAML frontmatter defining its metadata:

```markdown
---
Name: example_practice
TargetRole: Engineer
AreaOfConcern: CodeQuality
---
Your practice instructions go here.
```

### Supported Roles
- **Lead**: Orchestrates the session.
- **Architect**: Plans the approach.
- **Engineer**: Implements the code.
- **Reviewer**: Critiques the solution.


### Troubleshooting Docker in WSL

If you get access denied errors connecting to Docker:

1. **Enable WSL Integration** in Docker Desktop settings (if using Docker Desktop on Windows).
2. Or, if running native Docker in WSL, ensure your user is in the docker group:
   ```bash
   sudo usermod -aG docker $USER
   newgrp docker
   ```
3. Verify connectivity:
   ```bash
   docker run --rm hello-world
   ```

## Prompt Optimization (GEPA)

Bendover uses a unified **Replay Workflow** to optimize agent practices using [DSPy's GEPA](https://github.com/stanfordnlp/dspy). Instead of synthetic benchmarks, we use recorded real-world runs to evolve our prompt bundles.

### The Workflow

1.  **Capture**: Interactive sessions with the CLI (`Bendover.Presentation.CLI`) are automatically recorded to `.bendover/promptopt/runs/`. Each run captures the user's goal, the repository state (`base_commit.txt`), and the full interaction trace.
2.  **Curate**: Selected run IDs are listed in dataset files (e.g., `train.txt`) to define a training split.
3.  **Optimize**: The `run_gepa.py` script:
    *   Loads the training runs.
    *   Starts with an active bundle. If `.bendover/promptopt/active.json` is missing, it rebuilds `.bendover/promptopt/bundles/seed` from root `.bendover/practices` and `.bendover/agents`, then writes `active.json` pointing to `seed`.
    *   Builds GEPA reflection context from run artifacts (`goal`, `base_commit`, `git_diff`, `dotnet_test`/error, `dotnet_build`/error, and `outputs.json` summary).
    *   Proposes changes only for practices with evaluator `notes_by_practice` feedback in the current GEPA batch.
    *   Treats practices without practice-specific notes as fixed (no reflection trace, no mutation).
    *   **Replays** each run by creating a temporary task environment with the original goal and commit.
    *   Evaluates the candidate bundle using `Bendover.PromptOpt.CLI`.
    *   Uses the evaluation (score + notes + practice attribution) to evolve practice content for the next generation.

### How Practices And Rules Work

PromptOpt.CLI discovers evaluator rules automatically by loading all `IEvaluatorRule` implementations from the assembly. No manual rule list is required.

Rule execution is convention-driven:

- Practice name format: `practice_name`
- Rule class format: `PracticeNameRule`
- Matching is case-insensitive and separator-insensitive (`_`, `-`, casing differences are normalized)

Examples:

- `tdd_spirit` -> `TDDSpiritRule`
- `readme_hygiene` -> `ReadmeHygieneRule`

At evaluation time:

1. `selected_practices` are read from run artifacts (`outputs.json` lead output).
2. `all_practices` are read from the bundle under `practices/*.md` (frontmatter `Name`, fallback to filename stem).
3. If a rule matches at least one practice in `all_practices`, it is treated as practice-bound and runs only when a matching practice is selected.
4. If a rule matches no practice in `all_practices`, it is treated as a global rule and always runs (e.g., `ForbiddenFilesRule`).

Lead selection validation in replay:

- PromptOpt replay resolves bundle practices once per run and uses that same list for Lead selection and downstream agent prompts.
- If Lead returns no practices, replay fails fast with an explicit error.
- If Lead returns any practice name not present in the active bundle list, replay fails fast with an explicit error.
- This prevents writing invalid bundle-external names into `practice_attribution.selected_practices`.

Evaluator output is written as `evaluator.json` with stable snake_case fields:

- `pass`, `score`, `flags`, `notes`
- `practice_attribution.selected_practices`
- `practice_attribution.offending_practices`
- `practice_attribution.notes_by_practice`

### Running Optimization

#### 1. Requirements
- Python 3.10+
- `pip install -r promptopt/requirements.txt`
- A `.env` file in the project root (copied from `.env.example`).
- Root practices and prompts under `.bendover/practices` and `.bendover/agents` (used to bootstrap seed when `active.json` is missing).
- Recorded runs in `.bendover/promptopt/runs/`.

### Configuration
1. Copy `.env.example` to `.env` in the project root:
   ```bash
   cp .env.example .env
   ```
2. Configure your keys:
   - `Agent__*`: For local LLMs used by the Agent.
   - `OPENAI_*` / `DSPY_*`: For reflection models used by GEPA.

#### 2. Create a Split
Create a text file at `.bendover/promptopt/datasets/train.txt` listing the IDs of the runs you want to optimize against:
```text
20260130_100000_abc123
20260130_110000_def456
```

#### 3. Run GEPA
Execute the optimization script using the prompt optimization root. By default it reads `datasets/train.txt` under that root.

```bash
export PYTHONPATH=$PYTHONPATH:$(pwd)/src && ./src/promptopt/.venv/bin/python -m promptopt.run_gepa \
  --promptopt-root .bendover/promptopt \
  --cli-command "dotnet run --project src/Bendover.PromptOpt.CLI --" \
  --max-full-evals 10
```

Bundle lifecycle:
- If `.bendover/promptopt/active.json` exists, GEPA starts from that active bundle.
- If `active.json` is missing, GEPA rebuilds `bundles/seed` from root `.bendover/practices` and `.bendover/agents`, then sets `active.json` to `seed`.
- After optimization, GEPA writes new bundles under `.bendover/promptopt/bundles` and updates `active.json` to the best bundle.
- If you manually promote only part of a generated bundle, delete `active.json` before the next run to intentionally reset seed from current root practices/prompts.

Logs (GEPA + evaluator output) are written under `.bendover/promptopt/logs`.

If you need to bypass DSPy caches (e.g., for testing), add `--disable-dspy-cache`.

When running `Bendover.PromptOpt.CLI` manually, pass `--verbose` to print:
- Lead-selected practices parsed from `outputs.json`
- Evaluator summary (`pass`, `score`, selected/offending practices)

### Scoring an existing run

You can score an already recorded run without replaying agents:

```bash
dotnet run --project src/Bendover.PromptOpt.CLI -- --run-id <run_id> --verbose
```

Bundle resolution for `--run-id` mode:
- If `--bundle` is provided, that bundle path is used.
- Otherwise `bundle_id.txt` from the run is used.
- `bundle_id` values `current` (and legacy `default`) mean root `.bendover` practices.

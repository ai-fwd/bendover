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
    *   Starts with a **Seed Bundle** of practices.
    *   Proposes changes to a specific **Target Practice File** (e.g., `coding_standards.md`).
    *   **Replays** each run by creating a temporary task environment with the original goal and commit.
    *   Evaluates the candidate bundle using `Bendover.PromptOpt.CLI`.
    *   Uses feedback to evolve the practice content further.

### Running Optimization

#### 1. Requirements
- Python 3.10+
- `pip install -r promptopt/requirements.txt`
- A `.env` file in `promptopt/.env` with `DSPY_REFLECTION_MODEL` (optional, defaults to `gpt-4o-mini`).
- A seed bundle in `.bendover/promptopt/bundles/seed` (or similar).
- Recorded runs in `.bendover/promptopt/runs/`.

#### 2. Create a Split
Create a text file (e.g., `promptopt/datasets/train.txt`) listing the IDs of the runs you want to optimize against:
```text
20260130_100000_abc123
20260130_110000_def456
```

#### 3. Run GEPA
Execute the optimization script, specifying the target practice file you want to evolve.

```bash
export PYTHONPATH=$PYTHONPATH:.
python3 promptopt/run_gepa.py \
  --seed-bundle-id seed \
  --train-split promptopt/datasets/train.txt \
  --target-practice-file coding_standards.md \
  --log-dir .bendover/promptopt/logs \
  --cli-command "dotnet run --project src/Bendover.PromptOpt.CLI"
```

The optimizer will output the evolved body content for the target practice file.

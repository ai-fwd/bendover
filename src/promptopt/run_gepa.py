import typer
import dspy
import os
import json
from pathlib import Path
from dspy.teleprompt import GEPA
from dotenv import load_dotenv
from promptopt.gepa_driver import load_split, create_candidate_bundle, evaluate_bundle, prepare_replay_task

# Load env automatically (finds .env in root)
load_dotenv()

app = typer.Typer()

class TaskSignature(dspy.Signature):
    """
    Execute a task using the provided prompt bundle.
    """
    run_id: str = dspy.InputField(desc="ID of the run to replay")
    score: float = dspy.OutputField(desc="Evaluation score")

class BundleProgram(dspy.Module):
    def __init__(self, seed_bundle_path: Path, target_file: str, seed_body: str, run_ids: list, bundle_root: str, run_root: str, cli_command: str, log_dir: str, timeout: int):
        super().__init__()
        self.predictor = dspy.Predict(TaskSignature)
        # Initialize instruction with SEED BODY
        self.predictor.signature.instructions = seed_body
        
        self.seed_bundle_path = seed_bundle_path
        self.target_file = target_file
        self.run_ids = run_ids
        self.bundle_root = bundle_root
        self.run_root = run_root
        self.cli_command = cli_command
        self.log_dir = log_dir
        self.timeout = timeout
        
    def forward(self, run_id: str):
        # 1. Get current evolved body content
        current_body = self.predictor.signature.instructions
        
        # 2. Create Candidate Bundle
        # We pass ONLY the body content for the target file.
        # Frontmatter is preserved by create_candidate_bundle.
        practices_update = {self.target_file: current_body}
        
        bundle_id, bundle_path, _ = create_candidate_bundle(
            seed_bundle_path=self.seed_bundle_path,
            bundle_root=self.bundle_root,
            generation="opt", 
            practices_content=practices_update,
            exist_ok=True
        )
        
        # 3. Evaluate
        # We need to prepare the replay environment for THIS run.
        task_path = prepare_replay_task(run_id, self.run_root)
        
        passed, score = evaluate_bundle(
            bundle_path=bundle_path,
            task_path=task_path,
            cli_command=self.cli_command,
            log_dir=self.log_dir,
            timeout_seconds=self.timeout
        )
        
        return dspy.Prediction(score=score)

def metric_fn(gold, pred, trace=None, pred_name=None, pred_trace=None):
    return pred.score

@app.command()
def main(
    seed_bundle_id: str = typer.Option(..., help="ID of the starting bundle"),
    train_split: str = typer.Option(..., help="Path to train.txt"),
    log_dir: str = typer.Option(..., help="Directory for logs and run outputs"),
    cli_command: str = typer.Option(..., help="Command to invoke Bendover CLI"),
    target_practice_file: str = typer.Option(..., help="Practice file to evolve (e.g., coding_standards.md)"),
    timeout_seconds: int = typer.Option(900, help="Execution timeout"),
    lm_model: str = typer.Option(os.getenv("DSPY_REFLECTION_MODEL", "gpt-4o-mini"), help="LLM model to use"),
    bundle_root: str = typer.Option(".bendover/promptopt/bundles/", help="Bundle root directory"),
    run_root: str = typer.Option(".bendover/promptopt/runs/", help="Run root directory")
):
    # Setup Paths
    log_path = Path(log_dir)
    log_path.mkdir(parents=True, exist_ok=True)
    
    # Load Runs
    run_ids = load_split(train_split)
    trainset = [dspy.Example(run_id=rid).with_inputs("run_id") for rid in run_ids]
    
    # Setup LM
    lm = dspy.LM(lm_model)
    dspy.configure(lm=lm)
    
    # Initialize instruction (body content of target file)
    seed_bundle_path = Path(bundle_root) / seed_bundle_id
    if not seed_bundle_path.exists():
        raise FileNotFoundError(f"Seed bundle not found: {seed_bundle_path}")
        
    target_path = seed_bundle_path / "practices" / target_practice_file
    if not target_path.exists():
         # If target doesn't exist, we can't evolve it easily without knowing frontmatter.
         # For now, require it exists.
         raise FileNotFoundError(f"Target practice file not found: {target_path}")
    
    # Extract body for optimization validation
    original_text = target_path.read_text()
    if original_text.startswith("---\n"):
        parts = original_text.split("---\n", 2)
        if len(parts) >= 3:
            seed_body = parts[2].strip()
        else:
            seed_body = original_text
    else:
        seed_body = original_text

    program = BundleProgram(
        seed_bundle_path=seed_bundle_path,
        target_file=target_practice_file,
        seed_body=seed_body,
        run_ids=run_ids,
        bundle_root=bundle_root,
        run_root=run_root,
        cli_command=cli_command,
        log_dir=log_dir,
        timeout=timeout_seconds
    )
    
    # Optimization
    teleprompter = GEPA(metric=metric_fn, max_full_evals=10, reflection_lm=lm)
    
    print(f"Starting optimization with {len(run_ids)} runs...")
    compiled_program = teleprompter.compile(
        program,
        trainset=trainset,
    )
    
    print("Optimization Complete.")
    print("Best Body Content:")
    print(compiled_program.predictor.signature.instructions)

if __name__ == "__main__":
    app()

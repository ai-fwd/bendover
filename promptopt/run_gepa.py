import typer
import dspy
import os
import json
from pathlib import Path
from dspy.teleprompt import GEPA
from promptopt.gepa_driver import load_split, create_candidate_bundle, evaluate_bundle

app = typer.Typer()

class TaskSignature(dspy.Signature):
    """
    Execute a task using the provided prompt bundle.
    """
    task_path: str = dspy.InputField(desc="Path to the task directory")
    # We produce a score, but in DSPy flow, the 'prediction' is usually the output text. 
    # Here we are bypassing text generation and going straight to score via side-effect (CLI).
    # But GEPA optimizes the *Instruction* of this signature.
    score: float = dspy.OutputField(desc="Evaluation score")

class BundleProgram(dspy.Module):
    def __init__(self, seed_bundle_id: str, seed_instruction: str, bundle_root: str, cli_command: str, log_dir: str, timeout: int):
        super().__init__()
        self.predictor = dspy.Predict(TaskSignature)
        # Initialize instruction
        self.predictor.signature.instructions = seed_instruction
        
        self.seed_bundle_id = seed_bundle_id
        self.bundle_root = bundle_root
        self.cli_command = cli_command
        self.log_dir = log_dir
        self.timeout = timeout
        self.current_generation = 0 
        
    def forward(self, task_path: str):
        # 1. Get current instruction (optimized by GEPA)
        current_instruction = self.predictor.signature.instructions
        
        # 2. Create Bundle
        # We assume single practice file for now: "active.md" ? or "practice.md"
        # The seed bundle should tell us the filename? 
        # For MVP, we'll hardcode "practice.md" or read from seed if possible.
        # Let's assume "instructions.md".
        practices = {"instructions.md": current_instruction}
        
        # We need to handle generation logic. 
        # Since we don't know the exact generation from GEPA, we can try to guess or use timestamp/random.
        # Or just use 0 and let hash distinguish?
        # But `create_candidate_bundle` uses generation in ID.
        # Let's use a simple counter on the class? No, Program is cloned.
        # We'll use a simplistic generation=1 for all candidates during optimization, 
        # or rely on the fact that `create_candidate_bundle` handles collisions.
        
        bundle_id, bundle_path, _ = create_candidate_bundle(
            seed_bundle_id=self.seed_bundle_id,
            bundle_root=self.bundle_root,
            generation="opt", # Using 'opt' as generation placeholder
            practices_content=practices,
            exist_ok=True
        )
        
        # 3. Evaluate
        passed, score = evaluate_bundle(
            bundle_path=bundle_path,
            task_path=task_path,
            cli_command=self.cli_command,
            log_dir=self.log_dir,
            timeout_seconds=self.timeout
        )
        
        return dspy.Prediction(score=score)

def metric_fn(gold, pred, trace=None):
    return pred.score

@app.command()
def main(
    seed_bundle_id: str = typer.Option(..., help="ID of the starting bundle"),
    train_split: str = typer.Option(..., help="Path to train.txt"),
    log_dir: str = typer.Option(..., help="Directory for logs and run outputs"),
    cli_command: str = typer.Option(..., help="Command to invoke Bendover CLI"),
    timeout_seconds: int = typer.Option(900, help="Execution timeout"),
    lm_model: str = typer.Option("gpt-4o-mini", help="LLM model to use"),
    bundle_root: str = typer.Option(".bendover/promptopt/bundles/", help="Bundle root directory")
):
    # Setup Paths
    log_path = Path(log_dir)
    log_path.mkdir(parents=True, exist_ok=True)
    
    # Load Tasks
    tasks = load_split(train_split)
    trainset = [dspy.Example(task_path=t).with_inputs("task_path") for t in tasks]
    
    # Setup LM
    lm = dspy.LM(lm_model)
    dspy.configure(lm=lm)
    
    # Load Seed Instruction
    # Assume seed bundle exists and has "instructions.md"
    seed_path = Path(bundle_root) / seed_bundle_id / "practices" / "instructions.md"
    if not seed_path.exists():
        # Fallback or error?
        # For MVP/testing, if seed doesn't exist, maybe use placeholder?
        # But user said "Actions: Read evaluator.json", implies real files.
        # We'll just define a default if file missing for this first pass or raise.
        if (Path(bundle_root) / seed_bundle_id).exists():
             # Try finding any md file
             md_files = list((Path(bundle_root) / seed_bundle_id / "practices").glob("*.md"))
             if md_files:
                 seed_instruction = md_files[0].read_text()
             else:
                 seed_instruction = "You are a helpful assistant."
        else:
            seed_instruction = "You are a helpful assistant."
            
    program = BundleProgram(
        seed_bundle_id=seed_bundle_id,
        seed_instruction=seed_instruction,
        bundle_root=bundle_root,
        cli_command=cli_command,
        log_dir=log_dir,
        timeout=timeout_seconds
    )
    
    # Optimization
    # GEPA configuration can be tuning. Using defaults for now.
    teleprompter = GEPA(metric=metric_fn)
    
    print(f"Starting optimization with {len(tasks)} tasks...")
    compiled_program = teleprompter.compile(
        program,
        trainset=trainset,
    )
    
    # Save optimized instruction?
    # compiled_program.predictor.signature.instructions contains the best found.
    print("Optimization Complete.")
    print("Best Instruction:")
    print(compiled_program.predictor.signature.instructions)

if __name__ == "__main__":
    app()


import pytest
from typer.testing import CliRunner
from unittest.mock import MagicMock, patch
from promptopt.run_gepa import app, BundleProgram

runner = CliRunner()

@pytest.fixture
def mock_dependencies():
    with patch("promptopt.run_gepa.load_split") as mock_load, \
         patch("promptopt.run_gepa.GEPA") as mock_gepa, \
         patch("dspy.LM") as mock_lm, \
         patch("dspy.configure") as mock_config, \
         patch("promptopt.run_gepa.BundleProgram") as mock_program_cls:
         
        mock_load.return_value = ["task1", "task2"]
        
        # Mock GEPA instance
        mock_teleprompter = MagicMock()
        mock_gepa.return_value = mock_teleprompter
        
        # Mock Program instance
        mock_program = MagicMock()
        mock_program_cls.return_value = mock_program
        # Mock predictor
        mock_program.predictor.signature.instructions = "Mock Optimized Instruction"
        
        # Mock compiled program
        mock_compiled = MagicMock()
        mock_compiled.predictor.signature.instructions = "Best Instruction Found"
        mock_teleprompter.compile.return_value = mock_compiled

        
        yield {
            "load_split": mock_load,
            "GEPA": mock_gepa,
            "teleprompter": mock_teleprompter,
            "program_cls": mock_program_cls
        }

def test_cli_invocation_format(mock_dependencies):
    deps = mock_dependencies
    
    result = runner.invoke(app, [
        "--seed-bundle-id", "seed123",
        "--train-split", "train.txt",
        "--log-dir", "logs",
        "--cli-command", "bendover-cli",
        "--timeout-seconds", "30"
    ])
    
    assert result.exit_code == 0
    
    # Verify load_split called
    deps["load_split"].assert_called_with("train.txt")
    
    # Verify GEPA initialized
    deps["GEPA"].assert_called_once()
    
    # Verify compile called
    deps["teleprompter"].compile.assert_called_once()
    call_args = deps["teleprompter"].compile.call_args
    assert call_args[0][0] == deps["program_cls"].return_value # The program instance
    assert len(call_args[1]["trainset"]) == 2 # 2 tasks

def test_bundle_program_forward():
    # Test the forward method logic independently
    with patch("promptopt.run_gepa.create_candidate_bundle") as mock_create, \
         patch("promptopt.run_gepa.evaluate_bundle") as mock_eval:
         
         mock_create.return_value = ("id", "path", {})
         mock_eval.return_value = (True, 95.0)
         
         program = BundleProgram(
             seed_bundle_id="seed", 
             seed_instruction="instr", 
             bundle_root="root", 
             cli_command="cmd", 
             log_dir="logs", 
             timeout=10
         )
         
         # Mock predictor/signature which is usually set up by dspy.Module/Predict
         # Since we are not running full dspy, we need to ensure predictor structure exists
         # BundleProgram init creates self.predictor = dspy.Predict(TaskSignature)
         # We need to make sure self.predictor.extended_signature.instructions is accessible
         
         # NOTE: dspy.Predict might need real dspy internals working or mocked. 
         # Given we installed dspy, we can use the real class logic if minimal.
         # But usually dspy requires settings.configure logic or it's fine.
         
         pred = program.forward("task_path/t1")
         
         # Verify create_candidate_bundle called with current instruction
         mock_create.assert_called_once()
         assert mock_create.call_args[1]["exist_ok"] is True
         
         # Verify evaluate_bundle called
         mock_eval.assert_called_once()
         
         # Verify return score
         assert pred.score == 95.0

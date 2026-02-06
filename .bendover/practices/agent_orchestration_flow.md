---
Name: agent_orchestration_flow
TargetRole: Architect
AreaOfConcern: Agent Workflow
---
The core workflow is a fixed multi-agent pipeline coordinated in `AgentOrchestrator`:
- Lead selects practices based on metadata.
- Architect plans.
- Engineer implements (code is wrapped and executed in a container).
- Reviewer critiques the result.

Practices are loaded by `IPracticeService` and inserted into the system prompts for each phase. If you change the flow, ensure the selected practices still appear in each phase prompt and that prompt/output recording remains consistent.

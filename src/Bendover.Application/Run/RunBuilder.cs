using System.Runtime.ExceptionServices;
using Bendover.Application.Transcript;

namespace Bendover.Application.Run;

public sealed class RunBuilder
{
    private readonly RunStageFactory _stageFactory;
    private readonly List<Type> _stageTypes = new();
    private bool _streamTranscriptEnabled;

    private RunBuilder(RunStageFactory stageFactory)
    {
        _stageFactory = stageFactory ?? throw new ArgumentNullException(nameof(stageFactory));
    }

    public static RunBuilder Create(RunStageFactory stageFactory)
    {
        return new RunBuilder(stageFactory);
    }

    public RunBuilder Add<T>() where T : RunStage
    {
        _stageTypes.Add(typeof(T));
        return this;
    }

    public RunBuilder ConfigureTranscript(bool enabled)
    {
        _streamTranscriptEnabled = enabled;
        return this;
    }

    public RunDelegate Build()
    {
        var stages = _stageTypes
            .Select(_stageFactory.Create)
            .ToArray();

        var setupStages = stages
            .OrderBy(stage => stage.SetupOrder)
            .ToArray();
        var teardownStages = stages
            .OrderByDescending(stage => stage.TeardownOrder)
            .ToArray();

        return async (context, terminal) =>
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(terminal);

            context.TranscriptWriter = _streamTranscriptEnabled
                ? new EventTranscriptWriter(context.Events)
                : new NoOpTranscriptWriter();

            var activeStages = new List<RunStage>();
            Exception? pendingException = null;

            try
            {
                foreach (var stage in setupStages)
                {
                    activeStages.Add(stage);
                    await stage.SetupAsync(context);
                }

                foreach (var stage in setupStages)
                {
                    await stage.ExecuteAsync(context);
                }

                await terminal(context);
            }
            catch (Exception ex)
            {
                context.TerminalException = ex;
                pendingException = ex;
            }
            finally
            {
                foreach (var stage in teardownStages.Where(activeStages.Contains))
                {
                    try
                    {
                        await stage.TeardownAsync(context);
                    }
                    catch (Exception ex)
                    {
                        pendingException ??= ex;
                    }
                }
            }

            if (pendingException is not null)
            {
                ExceptionDispatchInfo.Capture(pendingException).Throw();
            }
        };
    }
}

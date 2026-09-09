using Spectre.Console;
using Tenninety.Execution;
using Tenninety.Tui;

namespace Tenninety.Tests;

public sealed class TuiExecutionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Startup_failure_is_observed_without_input_and_rendered_safely(bool synchronous)
    {
        const string secret = "supersecretvalue123";
        var error = new InvalidOperationException(
            "startup [red]failure[/] apiKey=" + secret + "; \u001b[31m\r\0\u0085" + new string('x', 5000));
        using var execution = new TuiExecution(
            _ => synchronous ? throw error : Task.FromException<OrchestratorExit>(error),
            () => { }, () => { }, () => Assert.Fail("An already failed run must not be stopped."));

        await execution.ObserveCompletedAsync();
        var output = Render(execution);

        Assert.False(execution.IsRunning);
        Assert.Null(execution.Exit);
        Assert.Equal("FAILED", execution.Status);
        Assert.Equal(1, await execution.ShutdownAsync());
        Assert.Contains("FAILED", output);
        Assert.Contains("startup [red]failure[/]", output);
        Assert.Contains("[REDACTED]", output);
        Assert.DoesNotContain(secret, output);
        Assert.DoesNotContain('\u001b', output);
        Assert.DoesNotContain('\r', output);
        Assert.DoesNotContain('\0', output);
        Assert.DoesNotContain('\u0085', output);
        Assert.True(execution.LastError!.Length <= 4000);
        Assert.Contains("[truncated]", execution.LastError);
    }

    [Fact]
    public async Task Mid_execution_failure_is_observed_on_refresh_without_a_keypress()
    {
        var run = new TaskCompletionSource<OrchestratorExit>();
        using var execution = new TuiExecution(_ => run.Task, () => { }, () => { }, () => { });
        await execution.ObserveCompletedAsync();
        Assert.True(execution.IsRunning);
        Assert.Contains("RUNNING", Render(execution));

        run.SetException(new IOException("mid-execution failure"));
        await execution.ObserveCompletedAsync();

        Assert.False(execution.IsRunning);
        Assert.Contains("FAILED", Render(execution));
        Assert.Contains("mid-execution failure", Render(execution));
        Assert.Equal(1, await execution.ShutdownAsync());
    }

    [Theory]
    [InlineData(OrchestratorExit.Completed, "COMPLETED", 0)]
    [InlineData(OrchestratorExit.Paused, "PAUSED", 0)]
    [InlineData(OrchestratorExit.Stopped, "STOPPED", 0)]
    [InlineData(OrchestratorExit.Deadlocked, "DEADLOCKED", 4)]
    [InlineData(OrchestratorExit.Cancelled, "CANCELLED", 1)]
    public async Task Completed_tasks_display_the_actual_exit_and_return_its_code(
        OrchestratorExit exit, string status, int code)
    {
        using var execution = new TuiExecution(_ => Task.FromResult(exit),
            () => { }, () => { }, () => Assert.Fail("An already completed run must not be stopped."));

        await execution.ObserveCompletedAsync();

        Assert.Equal(exit, execution.Exit);
        Assert.Equal(status, execution.Status);
        Assert.Contains(status, Render(execution));
        Assert.Null(execution.LastError);
        Assert.Equal(code, await execution.ShutdownAsync());
    }

    [Fact]
    public async Task Failure_during_pause_is_not_relabelled_paused_and_survives_resume_and_action_banners()
    {
        var run = new TaskCompletionSource<OrchestratorExit>();
        using var execution = new TuiExecution(_ => run.Task, () => { }, () => { }, () => { });
        var pause = execution.PauseAsync();
        Assert.Contains("pausing", Render(execution));
        run.SetException(new IOException("failure while pausing"));
        await pause;

        Assert.Equal("FAILED", execution.Status);
        Assert.Null(execution.Banner);
        var error = execution.LastError;
        run = new TaskCompletionSource<OrchestratorExit>();
        await execution.ResumeAsync();

        Assert.Equal("RUNNING", execution.Status);
        Assert.Contains("resumed", Render(execution));
        Assert.Contains(error!, Render(execution));
        pause = execution.PauseAsync();
        Assert.Contains(error!, Render(execution));
        run.SetResult(OrchestratorExit.Paused);
        await pause;
        execution.Banner = "Pivot discarded - daemon still paused.";
        await execution.ObserveCompletedAsync();

        Assert.Equal("PAUSED", execution.Status);
        Assert.Equal(error, execution.LastError);
        Assert.Contains(execution.Banner, Render(execution));
        Assert.Contains(error!, Render(execution));
        Assert.Equal(0, await execution.ShutdownAsync());
    }

    [Theory]
    [InlineData(OrchestratorExit.Completed)]
    [InlineData(OrchestratorExit.Stopped)]
    [InlineData(OrchestratorExit.Deadlocked)]
    [InlineData(OrchestratorExit.Cancelled)]
    public async Task Pause_request_does_not_override_the_actual_exit(OrchestratorExit exit)
    {
        var run = new TaskCompletionSource<OrchestratorExit>();
        using var execution = new TuiExecution(_ => run.Task,
            () => run.SetResult(exit), () => { }, () => { });

        await execution.PauseAsync();

        Assert.Equal(exit, execution.Exit);
        Assert.NotEqual("PAUSED", execution.Status);
        Assert.Null(execution.Banner);
    }

    [Fact]
    public async Task Completed_run_clears_a_pause_request_latched_during_the_completion_race()
    {
        var run = new TaskCompletionSource<OrchestratorExit>();
        var pauseLatched = false;
        using var execution = new TuiExecution(_ => run.Task,
            () =>
            {
                run.SetResult(OrchestratorExit.Completed);
                pauseLatched = true;
            },
            () => { }, () => { }, () => pauseLatched = false);

        await execution.PauseAsync();

        Assert.Equal(OrchestratorExit.Completed, execution.Exit);
        Assert.False(pauseLatched);
    }

    [Fact]
    public async Task Resume_observes_the_previous_task_before_replacing_it_and_captures_a_new_failure()
    {
        var run = new TaskCompletionSource<OrchestratorExit>();
        using var execution = new TuiExecution(_ => run.Task, () => { }, () => { }, () => { });
        run.SetException(new IOException("first failure"));
        run = new TaskCompletionSource<OrchestratorExit>();

        await execution.ResumeAsync();

        Assert.Equal("RUNNING", execution.Status);
        Assert.Contains("first failure", execution.LastError);
        run.SetException(new IOException("retry failure"));
        await execution.ObserveCompletedAsync();
        Assert.Equal("FAILED", execution.Status);
        Assert.Null(execution.Banner);
        Assert.Contains("retry failure", Render(execution));
        Assert.Equal(1, await execution.ShutdownAsync());
    }

    [Fact]
    public async Task Normal_pause_and_resume_can_complete_successfully()
    {
        var run = new TaskCompletionSource<OrchestratorExit>();
        var resumes = 0;
        using var execution = new TuiExecution(_ => run.Task,
            () => run.SetResult(OrchestratorExit.Paused), () => resumes++, () => { });

        await execution.PauseAsync();
        Assert.Equal("PAUSED", execution.Status);
        run = new TaskCompletionSource<OrchestratorExit>();
        await execution.ResumeAsync();
        Assert.Equal(1, resumes);
        Assert.Equal("RUNNING", execution.Status);
        run.SetResult(OrchestratorExit.Completed);
        await execution.ObserveCompletedAsync();

        Assert.Equal("COMPLETED", execution.Status);
        Assert.Null(execution.Banner);
        Assert.Null(execution.LastError);
        Assert.Equal(0, await execution.ShutdownAsync());
    }

    [Fact]
    public async Task Resume_control_failure_is_not_hidden_by_a_resumed_banner()
    {
        using var execution = new TuiExecution(_ => Task.FromResult(OrchestratorExit.Paused),
            () => { }, () => throw new IOException("resume refused"), () => { });

        await execution.ResumeAsync();

        Assert.Equal("FAILED", execution.Status);
        Assert.Null(execution.Banner);
        Assert.Contains("resume refused", Render(execution));
        Assert.Equal(1, await execution.ShutdownAsync());
    }

    [Fact]
    public async Task Shutdown_requests_a_safe_stop_without_cancelling_a_cooperative_run()
    {
        var run = new TaskCompletionSource<OrchestratorExit>();
        CancellationToken token = default;
        using var execution = new TuiExecution(ct => { token = ct; return run.Task; },
            () => { }, () => { }, () => run.SetResult(OrchestratorExit.Stopped));

        Assert.Equal(0, await execution.ShutdownAsync());
        Assert.Equal("STOPPED", execution.Status);
        Assert.False(token.IsCancellationRequested);
    }

    [Fact]
    public async Task Shutdown_cancels_after_the_grace_period_and_observes_cancellation()
    {
        var stops = 0;
        using var execution = new TuiExecution(async ct =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return OrchestratorExit.Completed;
        }, () => { }, () => { }, () => stops++);

        Assert.Equal(1, await execution.ShutdownAsync(TimeSpan.Zero).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, stops);
        Assert.Equal(OrchestratorExit.Cancelled, execution.Exit);
        Assert.Equal("CANCELLED", execution.Status);
        Assert.Null(execution.LastError);
    }

    [Fact]
    public async Task Signal_shutdown_starts_the_grace_period_before_the_ui_can_await_cleanup()
    {
        var stops = 0;
        using var execution = new TuiExecution(async ct =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return OrchestratorExit.Completed;
        }, () => { }, () => { }, () => stops++);

        execution.RequestShutdown(TimeSpan.Zero);
        var code = await execution.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, code);
        Assert.Equal(1, stops);
        Assert.Equal(OrchestratorExit.Cancelled, execution.Exit);
    }

    [Fact]
    public async Task Shutdown_observes_a_failure_after_stop_is_requested()
    {
        var run = new TaskCompletionSource<OrchestratorExit>();
        using var execution = new TuiExecution(_ => run.Task, () => { }, () => { },
            () => run.SetException(new IOException("shutdown failure")));

        Assert.Equal(1, await execution.ShutdownAsync());
        Assert.Equal("FAILED", execution.Status);
        Assert.Contains("shutdown failure", Render(execution));
    }

    private static string Render(TuiExecution execution)
    {
        using var output = new StringWriter { NewLine = "\n" };
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(output),
        });
        TuiHost.WriteExecutionStatus(console, execution);
        return output.ToString();
    }
}

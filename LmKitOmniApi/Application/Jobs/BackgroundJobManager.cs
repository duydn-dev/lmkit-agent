using Hangfire;

namespace LmKitOmniApi.Application.Jobs;

public class BackgroundJobManager
{
    public void ScheduleJob(string jobName, Action jobAction)
    {
        BackgroundJob.Enqueue(() => ExecuteJob(jobName, jobAction));
    }

    [JobDisplayName("{0}")]
    public static void ExecuteJob(string jobName, Action jobAction)
    {
        Console.WriteLine($"[JobStarted] {jobName}");
        try
        {
            jobAction();
            Console.WriteLine($"[JobCompleted] {jobName}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[JobFailed] {jobName} - {ex.Message}");
            throw; // Let Hangfire handle retries
        }
    }
}

namespace LmKitOmniApi.Application.Jobs;

// Mock for TickerQ Job Manager
public class BackgroundJobManager
{
    public void ScheduleJob(string jobName, Action jobAction)
    {
        // Simulate scheduling a background job
        Task.Run(() =>
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
            }
        });
    }
}

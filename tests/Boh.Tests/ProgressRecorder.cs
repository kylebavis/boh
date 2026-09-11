using Boh.Web.Jobs;

namespace Boh.Tests;

/// <summary>
/// Collects progress reports as they are made. <see cref="Progress{T}"/> would post each one to
/// the thread pool, so a test could read the list before the last report had landed.
/// </summary>
public sealed class ProgressRecorder : IProgress<JobProgress>
{
    private readonly List<JobProgress> _reports = [];

    public IReadOnlyList<JobProgress> Reports => _reports;

    public void Report(JobProgress value) => _reports.Add(value);
}

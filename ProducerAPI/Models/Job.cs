namespace ProducerAPI.Models;

public class Job
{
    public Guid Id { get; set; }
    public string Text { get; set; } = string.Empty;
    public string ProcessedText { get; set; } = string.Empty;
    public JobStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
}

public enum JobStatus
{
    Pending = 0,
    Processing = 1,
    Completed = 2,
    Failed = 3
}


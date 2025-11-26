namespace ProducerAPI.Models;

public class Job
{
    public Guid Id { get; set; }
    public string Text { get; set; } = string.Empty;
    public string ProcessedText { get; set; } = string.Empty;
    public JobStatus Status { get; set; }
    public JobType Type { get; set; } = JobType.Uppercase;
    public DateTime CreatedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public Guid UserId { get; set; }
    public User? User { get; set; }
}

public enum JobStatus
{
    Pending = 0,
    Processing = 1,
    Completed = 2,
    Failed = 3
}

public enum JobType
{
    Uppercase = 0,
    Lowercase = 1,
    Reverse = 2,
    CountWords = 3,
    Translate = 4
}


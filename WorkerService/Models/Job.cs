namespace WorkerService.Models;

public class Job
{
    public Guid Id { get; set; }
    public string Text { get; set; } = string.Empty;
    public string ProcessedText { get; set; } = string.Empty;
    public JobStatus Status { get; set; }
    public JobType Type { get; set; }
    public JobPriority Priority { get; set; } = JobPriority.Normal;
    public DateTime CreatedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public Guid UserId { get; set; }
    
    // File support
    public string? OriginalFileName { get; set; }
    public string? ProcessedFileName { get; set; }
    public string? FileContentType { get; set; }
    public long? FileSize { get; set; }
    public byte[]? FileData { get; set; }
    public byte[]? ProcessedFileData { get; set; }
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

public enum JobPriority
{
    Low = 0,
    Normal = 1,
    High = 2,
    Critical = 3
}


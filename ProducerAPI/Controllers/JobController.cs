using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ProducerAPI.Data;
using ProducerAPI.Hubs;
using ProducerAPI.Models;

namespace ProducerAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class JobController : ControllerBase
{
    private readonly IConnection _connection;
    private readonly JobDbContext _dbContext;
    private readonly IHubContext<JobHub> _hubContext;
    private readonly ILogger<JobController> _logger;
    private static string GetQueueName(JobType jobType, JobPriority priority = JobPriority.Normal)
    {
        var baseQueue = jobType switch
        {
            JobType.Uppercase => "job_queue_uppercase",
            JobType.Lowercase => "job_queue_lowercase",
            JobType.Reverse => "job_queue_reverse",
            JobType.CountWords => "job_queue_countwords",
            JobType.Translate => "job_queue_translate",
            _ => "job_queue_default"
        };
        
        // Add priority suffix for high priority jobs
        if (priority == JobPriority.High || priority == JobPriority.Critical)
        {
            return $"{baseQueue}_priority";
        }
        
        return baseQueue;
    }

    public JobController(IConnection connection, JobDbContext dbContext, IHubContext<JobHub> hubContext, ILogger<JobController> logger)
    {
        _connection = connection;
        _dbContext = dbContext;
        _hubContext = hubContext;
        _logger = logger;
    }

    [HttpGet("health")]
    [AllowAnonymous]
    public IActionResult Health()
    {
        return Ok(new { Status = "Healthy", Timestamp = DateTime.UtcNow });
    }

    [HttpPost("notify-update")]
    [AllowAnonymous]
    public async Task<IActionResult> NotifyJobUpdate([FromBody] JobUpdateNotification notification)
    {
        try
        {
            _logger.LogInformation($"Received job update notification: {notification.Id}, Status: {notification.Status}");
            await _hubContext.Clients.All.SendAsync("JobUpdated", notification);
            _logger.LogInformation($"SignalR notification sent to clients for job: {notification.Id}");
            return Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error notifying job update");
            return StatusCode(500, "Internal server error");
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetJobs()
    {
        try
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (userIdClaim == null || !Guid.TryParse(userIdClaim, out var userId))
            {
                return Unauthorized();
            }

            var isAdmin = User.IsInRole("Admin");
            var query = _dbContext.Jobs.AsQueryable();

            // Admin sees all jobs, users see only their own
            if (!isAdmin)
            {
                query = query.Where(j => j.UserId == userId);
            }

            var jobs = await query
                .OrderByDescending(j => j.CreatedAt)
                .Take(100)
                .Select(j => new
                {
                    j.Id,
                    j.Text,
                    j.ProcessedText,
                    j.Status,
                    j.Type,
                    j.Priority,
                    j.CreatedAt,
                    j.ProcessedAt,
                    j.UserId,
                    j.OriginalFileName,
                    j.ProcessedFileName,
                    j.FileSize,
                    HasFile = j.FileData != null,
                    HasProcessedFile = j.ProcessedFileData != null
                })
                .ToListAsync();

            return Ok(jobs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving jobs");
            return StatusCode(500, "Internal server error");
        }
    }

    [HttpPost]
    public async Task<IActionResult> CreateJob([FromBody] JobRequest request)
    {
        if (string.IsNullOrEmpty(request?.Text))
        {
            return BadRequest("Text field is required");
        }

        try
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (userIdClaim == null || !Guid.TryParse(userIdClaim, out var userId))
            {
                return Unauthorized("User ID not found in token");
            }

            // Verify user exists in database
            var userExists = await _dbContext.Users.AnyAsync(u => u.Id == userId);
            if (!userExists)
            {
                _logger.LogWarning($"User {userId} from token does not exist in database");
                return Unauthorized("User not found");
            }

            var jobType = request.Type ?? JobType.Uppercase;
            var priority = request.Priority ?? JobPriority.Normal;
            var job = new Job
            {
                Id = Guid.NewGuid(),
                Text = request.Text,
                Status = JobStatus.Pending,
                Type = jobType,
                Priority = priority,
                CreatedAt = DateTime.UtcNow,
                UserId = userId
            };

            // Save to database
            _dbContext.Jobs.Add(job);
            await _dbContext.SaveChangesAsync();

            // Send to RabbitMQ - use priority queue for high priority jobs
            var queueName = GetQueueName(jobType, priority);
            using var channel = _connection.CreateModel();
            channel.QueueDeclare(queue: queueName, durable: false, exclusive: false, autoDelete: false, arguments: null);

            var message = JsonSerializer.Serialize(new
            {
                Id = job.Id.ToString(),
                Text = job.Text,
                Type = (int)job.Type,
                Priority = (int)job.Priority,
                CreatedAt = job.CreatedAt,
                UserId = job.UserId.ToString()
            });
            var body = Encoding.UTF8.GetBytes(message);

            // Set message priority for RabbitMQ
            var properties = channel.CreateBasicProperties();
            if (priority == JobPriority.Critical)
            {
                properties.Priority = 10;
            }
            else if (priority == JobPriority.High)
            {
                properties.Priority = 7;
            }
            else if (priority == JobPriority.Normal)
            {
                properties.Priority = 5;
            }
            else
            {
                properties.Priority = 1;
            }

            channel.BasicPublish(exchange: "", routingKey: queueName, basicProperties: properties, body: body);

            _logger.LogInformation($"Job created: {job.Id} with type: {jobType}, text: {request.Text}");

            // Notify clients via SignalR
            await _hubContext.Clients.All.SendAsync("JobCreated", new
            {
                Id = job.Id.ToString(),
                Text = job.Text,
                Type = (int)job.Type,
                Priority = (int)job.Priority,
                Status = (int)job.Status,
                CreatedAt = job.CreatedAt
            });

            return Ok(new { JobId = job.Id, Message = "Job queued successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating job");
            return StatusCode(500, "Internal server error");
        }
    }

    [HttpPost("upload")]
    public async Task<IActionResult> UploadFile([FromForm] IFormFile file, [FromForm] JobType type, [FromForm] JobPriority? priority)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest("File is required");
        }

        if (file.Length > 10 * 1024 * 1024) // 10MB limit
        {
            return BadRequest("File size exceeds 10MB limit");
        }

        try
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (userIdClaim == null || !Guid.TryParse(userIdClaim, out var userId))
            {
                return Unauthorized("User ID not found in token");
            }

            // Verify user exists in database
            var userExists = await _dbContext.Users.AnyAsync(u => u.Id == userId);
            if (!userExists)
            {
                _logger.LogWarning($"User {userId} from token does not exist in database");
                return Unauthorized("User not found");
            }

            // Read file content
            using var memoryStream = new MemoryStream();
            await file.CopyToAsync(memoryStream);
            var fileData = memoryStream.ToArray();

            // Extract text from file (simple implementation - for text files)
            string text = string.Empty;
            if (file.ContentType?.StartsWith("text/") == true || 
                file.FileName?.EndsWith(".txt") == true ||
                file.FileName?.EndsWith(".csv") == true)
            {
                text = Encoding.UTF8.GetString(fileData);
            }
            else
            {
                // For binary files, use filename as text
                text = file.FileName;
            }

            var jobType = type;
            var jobPriority = priority ?? JobPriority.Normal;
            var job = new Job
            {
                Id = Guid.NewGuid(),
                Text = text,
                Status = JobStatus.Pending,
                Type = jobType,
                Priority = jobPriority,
                CreatedAt = DateTime.UtcNow,
                UserId = userId,
                OriginalFileName = file.FileName,
                FileContentType = file.ContentType,
                FileSize = file.Length,
                FileData = fileData
            };

            _dbContext.Jobs.Add(job);
            await _dbContext.SaveChangesAsync();

            // Send to RabbitMQ
            var queueName = GetQueueName(jobType, jobPriority);
            using var channel = _connection.CreateModel();
            channel.QueueDeclare(queue: queueName, durable: false, exclusive: false, autoDelete: false, arguments: null);

            var message = JsonSerializer.Serialize(new
            {
                Id = job.Id.ToString(),
                Text = job.Text,
                Type = (int)job.Type,
                Priority = (int)job.Priority,
                CreatedAt = job.CreatedAt,
                UserId = job.UserId.ToString(),
                HasFile = true,
                FileName = job.OriginalFileName
            });
            var body = Encoding.UTF8.GetBytes(message);

            var properties = channel.CreateBasicProperties();
            if (jobPriority == JobPriority.Critical) properties.Priority = 10;
            else if (jobPriority == JobPriority.High) properties.Priority = 7;
            else if (jobPriority == JobPriority.Normal) properties.Priority = 5;
            else properties.Priority = 1;

            channel.BasicPublish(exchange: "", routingKey: queueName, basicProperties: properties, body: body);

            await _hubContext.Clients.All.SendAsync("JobCreated", new
            {
                Id = job.Id.ToString(),
                Text = job.Text,
                Type = (int)job.Type,
                Priority = (int)job.Priority,
                Status = (int)job.Status,
                CreatedAt = job.CreatedAt,
                HasFile = true,
                FileName = job.OriginalFileName
            });

            return Ok(new { JobId = job.Id, Message = "File uploaded and job queued successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error uploading file");
            return StatusCode(500, "Internal server error");
        }
    }

    [HttpGet("{id}/download")]
    public async Task<IActionResult> DownloadResult(Guid id)
    {
        try
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (userIdClaim == null || !Guid.TryParse(userIdClaim, out var userId))
            {
                return Unauthorized();
            }

            var isAdmin = User.IsInRole("Admin");
            var job = await _dbContext.Jobs.FirstOrDefaultAsync(j => j.Id == id);

            if (job == null)
            {
                return NotFound();
            }

            if (!isAdmin && job.UserId != userId)
            {
                return Forbid();
            }

            if (job.Status != JobStatus.Completed)
            {
                return BadRequest("Job is not completed yet");
            }

            if (job.ProcessedFileData == null && job.ProcessedText == null)
            {
                return BadRequest("No processed result available");
            }

            // If we have processed file, return it
            if (job.ProcessedFileData != null && !string.IsNullOrEmpty(job.ProcessedFileName))
            {
                return File(job.ProcessedFileData, job.FileContentType ?? "application/octet-stream", job.ProcessedFileName);
            }

            // Otherwise, return processed text as file
            var textBytes = Encoding.UTF8.GetBytes(job.ProcessedText ?? string.Empty);
            var fileName = job.OriginalFileName != null 
                ? $"processed_{job.OriginalFileName}" 
                : $"result_{job.Id}.txt";
            
            return File(textBytes, "text/plain", fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error downloading result");
            return StatusCode(500, "Internal server error");
        }
    }

    [HttpGet("statistics")]
    public async Task<IActionResult> GetStatistics()
    {
        try
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (userIdClaim == null || !Guid.TryParse(userIdClaim, out var userId))
            {
                return Unauthorized();
            }

            var isAdmin = User.IsInRole("Admin");
            var query = _dbContext.Jobs.AsQueryable();

            if (!isAdmin)
            {
                query = query.Where(j => j.UserId == userId);
            }

            var totalJobs = await query.CountAsync();
            var completedJobs = await query.CountAsync(j => j.Status == JobStatus.Completed);
            var pendingJobs = await query.CountAsync(j => j.Status == JobStatus.Pending);
            var processingJobs = await query.CountAsync(j => j.Status == JobStatus.Processing);
            var failedJobs = await query.CountAsync(j => j.Status == JobStatus.Failed);

            var jobsByType = await query
                .GroupBy(j => j.Type)
                .Select(g => new { Type = g.Key, Count = g.Count() })
                .ToListAsync();

            var jobsByPriority = await query
                .GroupBy(j => j.Priority)
                .Select(g => new { Priority = g.Key, Count = g.Count() })
                .ToListAsync();

            // Calculate average processing time - need to materialize first for EF Core
            var completedJobsWithTimes = await query
                .Where(j => j.ProcessedAt != null)
                .Select(j => new { j.ProcessedAt, j.CreatedAt })
                .ToListAsync();
            
            var avgProcessingTime = completedJobsWithTimes.Any()
                ? completedJobsWithTimes.Average(j => (j.ProcessedAt!.Value - j.CreatedAt).TotalSeconds)
                : 0.0;

            var recentJobs = await query
                .OrderByDescending(j => j.CreatedAt)
                .Take(10)
                .Select(j => new
                {
                    j.Id,
                    j.Type,
                    j.Priority,
                    j.Status,
                    j.CreatedAt
                })
                .ToListAsync();

            return Ok(new
            {
                TotalJobs = totalJobs,
                Completed = completedJobs,
                Pending = pendingJobs,
                Processing = processingJobs,
                Failed = failedJobs,
                JobsByType = jobsByType,
                JobsByPriority = jobsByPriority,
                AverageProcessingTimeSeconds = avgProcessingTime,
                RecentJobs = recentJobs
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving statistics");
            return StatusCode(500, "Internal server error");
        }
    }

    [HttpGet("export")]
    public async Task<IActionResult> ExportJobs([FromQuery] string? format = "csv")
    {
        try
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (userIdClaim == null || !Guid.TryParse(userIdClaim, out var userId))
            {
                return Unauthorized();
            }

            var isAdmin = User.IsInRole("Admin");
            var query = _dbContext.Jobs.AsQueryable();

            if (!isAdmin)
            {
                query = query.Where(j => j.UserId == userId);
            }

            var jobs = await query
                .OrderByDescending(j => j.CreatedAt)
                .Select(j => new
                {
                    j.Id,
                    j.Text,
                    j.ProcessedText,
                    j.Status,
                    j.Type,
                    j.Priority,
                    j.CreatedAt,
                    j.ProcessedAt,
                    j.OriginalFileName
                })
                .ToListAsync();

            if (format?.ToLower() == "json")
            {
                return File(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(jobs, new JsonSerializerOptions { WriteIndented = true })), 
                    "application/json", 
                    $"jobs_export_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
            }

            // CSV format
            var csv = new StringBuilder();
            csv.AppendLine("Id,Text,ProcessedText,Status,Type,Priority,CreatedAt,ProcessedAt,FileName");
            foreach (var job in jobs)
            {
                csv.AppendLine($"{job.Id},\"{job.Text?.Replace("\"", "\"\"")}\",\"{job.ProcessedText?.Replace("\"", "\"\"")}\",{job.Status},{job.Type},{job.Priority},{job.CreatedAt:yyyy-MM-dd HH:mm:ss},{job.ProcessedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? ""},{job.OriginalFileName ?? ""}");
            }

            return File(Encoding.UTF8.GetBytes(csv.ToString()), 
                "text/csv", 
                $"jobs_export_{DateTime.UtcNow:yyyyMMdd_HHmmss}.csv");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting jobs");
            return StatusCode(500, "Internal server error");
        }
    }
}

public class JobRequest
{
    public string Text { get; set; } = string.Empty;
    public JobType? Type { get; set; }
    public JobPriority? Priority { get; set; }
}

public class JobUpdateNotification
{
    public string Id { get; set; } = string.Empty;
    public int Status { get; set; }
    public string? ProcessedText { get; set; }
    public DateTime? ProcessedAt { get; set; }
}


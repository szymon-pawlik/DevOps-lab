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
    private static string GetQueueName(JobType jobType)
    {
        return jobType switch
        {
            JobType.Uppercase => "job_queue_uppercase",
            JobType.Lowercase => "job_queue_lowercase",
            JobType.Reverse => "job_queue_reverse",
            JobType.CountWords => "job_queue_countwords",
            JobType.Translate => "job_queue_translate",
            _ => "job_queue_default"
        };
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
                    j.CreatedAt,
                    j.ProcessedAt,
                    j.UserId
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
            var job = new Job
            {
                Id = Guid.NewGuid(),
                Text = request.Text,
                Status = JobStatus.Pending,
                Type = jobType,
                CreatedAt = DateTime.UtcNow,
                UserId = userId
            };

            // Save to database
            _dbContext.Jobs.Add(job);
            await _dbContext.SaveChangesAsync();

            // Send to RabbitMQ - use different queue for different job types
            var queueName = GetQueueName(jobType);
            using var channel = _connection.CreateModel();
            channel.QueueDeclare(queue: queueName, durable: false, exclusive: false, autoDelete: false, arguments: null);

            var message = JsonSerializer.Serialize(new
            {
                Id = job.Id.ToString(),
                Text = job.Text,
                Type = (int)job.Type,
                CreatedAt = job.CreatedAt
            });
            var body = Encoding.UTF8.GetBytes(message);

            channel.BasicPublish(exchange: "", routingKey: queueName, basicProperties: null, body: body);

            _logger.LogInformation($"Job created: {job.Id} with type: {jobType}, text: {request.Text}");

            // Notify clients via SignalR
            await _hubContext.Clients.All.SendAsync("JobCreated", new
            {
                Id = job.Id.ToString(),
                Text = job.Text,
                Type = (int)job.Type,
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
}

public class JobRequest
{
    public string Text { get; set; } = string.Empty;
    public JobType? Type { get; set; }
}

public class JobUpdateNotification
{
    public string Id { get; set; } = string.Empty;
    public int Status { get; set; }
    public string? ProcessedText { get; set; }
    public DateTime? ProcessedAt { get; set; }
}


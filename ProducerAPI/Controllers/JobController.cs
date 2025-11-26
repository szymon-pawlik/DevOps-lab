using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using System.Text;
using System.Text.Json;
using ProducerAPI.Data;
using ProducerAPI.Models;

namespace ProducerAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class JobController : ControllerBase
{
    private readonly IConnection _connection;
    private readonly JobDbContext _dbContext;
    private readonly ILogger<JobController> _logger;
    private const string QueueName = "job_queue";

    public JobController(IConnection connection, JobDbContext dbContext, ILogger<JobController> logger)
    {
        _connection = connection;
        _dbContext = dbContext;
        _logger = logger;
    }

    [HttpGet("health")]
    public IActionResult Health()
    {
        return Ok(new { Status = "Healthy", Timestamp = DateTime.UtcNow });
    }

    [HttpGet]
    public async Task<IActionResult> GetJobs()
    {
        try
        {
            var jobs = await _dbContext.Jobs
                .OrderByDescending(j => j.CreatedAt)
                .Take(100)
                .Select(j => new
                {
                    j.Id,
                    j.Text,
                    j.ProcessedText,
                    j.Status,
                    j.CreatedAt,
                    j.ProcessedAt
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
            var job = new Job
            {
                Id = Guid.NewGuid(),
                Text = request.Text,
                Status = JobStatus.Pending,
                CreatedAt = DateTime.UtcNow
            };

            // Save to database
            _dbContext.Jobs.Add(job);
            await _dbContext.SaveChangesAsync();

            // Send to RabbitMQ
            using var channel = _connection.CreateModel();
            channel.QueueDeclare(queue: QueueName, durable: false, exclusive: false, autoDelete: false, arguments: null);

            var message = JsonSerializer.Serialize(new
            {
                Id = job.Id.ToString(),
                Text = job.Text,
                CreatedAt = job.CreatedAt
            });
            var body = Encoding.UTF8.GetBytes(message);

            channel.BasicPublish(exchange: "", routingKey: QueueName, basicProperties: null, body: body);

            _logger.LogInformation($"Job created: {job.Id} with text: {request.Text}");

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
}


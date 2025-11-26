using Microsoft.AspNetCore.Mvc;
using RabbitMQ.Client;
using System.Text;
using System.Text.Json;

namespace ProducerAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class JobController : ControllerBase
{
    private readonly IConnection _connection;
    private readonly ILogger<JobController> _logger;
    private const string QueueName = "job_queue";

    public JobController(IConnection connection, ILogger<JobController> logger)
    {
        _connection = connection;
        _logger = logger;
    }

    [HttpGet("health")]
    public IActionResult Health()
    {
        return Ok(new { Status = "Healthy", Timestamp = DateTime.UtcNow });
    }

    [HttpPost]
    public IActionResult CreateJob([FromBody] JobRequest request)
    {
        if (string.IsNullOrEmpty(request?.Text))
        {
            return BadRequest("Text field is required");
        }

        try
        {
            using var channel = _connection.CreateModel();
            channel.QueueDeclare(queue: QueueName, durable: false, exclusive: false, autoDelete: false, arguments: null);

            var job = new
            {
                Id = Guid.NewGuid().ToString(),
                Text = request.Text,
                CreatedAt = DateTime.UtcNow
            };

            var message = JsonSerializer.Serialize(job);
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


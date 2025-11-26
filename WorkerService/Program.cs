using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var configuration = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddEnvironmentVariables()
    .Build();

var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
var logger = loggerFactory.CreateLogger<Program>();

var rabbitMQHost = configuration["RabbitMQ:Host"] ?? "rabbitmq";
var rabbitMQPort = configuration.GetValue<int>("RabbitMQ:Port", 5672);
var rabbitMQUsername = configuration["RabbitMQ:Username"] ?? "guest";
var rabbitMQPassword = configuration["RabbitMQ:Password"] ?? "guest";
var queueName = "job_queue";

logger.LogInformation($"Connecting to RabbitMQ at {rabbitMQHost}:{rabbitMQPort}");

var factory = new ConnectionFactory()
{
    HostName = rabbitMQHost,
    Port = rabbitMQPort,
    UserName = rabbitMQUsername,
    Password = rabbitMQPassword
};

IConnection? connection = null;
IModel? channel = null;

try
{
    // Wait for RabbitMQ to be ready
    var maxRetries = 30;
    var retryCount = 0;
    while (retryCount < maxRetries)
    {
        try
        {
            connection = factory.CreateConnection();
            channel = connection.CreateModel();
            logger.LogInformation("Successfully connected to RabbitMQ");
            break;
        }
        catch (Exception ex)
        {
            retryCount++;
            logger.LogWarning($"Failed to connect to RabbitMQ (attempt {retryCount}/{maxRetries}): {ex.Message}");
            if (retryCount < maxRetries)
            {
                await Task.Delay(2000);
            }
            else
            {
                throw;
            }
        }
    }

    if (channel == null)
    {
        throw new Exception("Failed to create channel");
    }

    channel.QueueDeclare(queue: queueName, durable: false, exclusive: false, autoDelete: false, arguments: null);

    var consumer = new EventingBasicConsumer(channel);
    consumer.Received += (model, ea) =>
    {
        var body = ea.Body.ToArray();
        var message = Encoding.UTF8.GetString(body);
        
        try
        {
            var job = JsonSerializer.Deserialize<JobMessage>(message);
            if (job != null)
            {
                logger.LogInformation($"Processing job {job.Id}: {job.Text}");
                
                // Simulate processing work
                Thread.Sleep(2000);
                
                // Process text (convert to uppercase)
                var processedText = job.Text.ToUpper();
                
                logger.LogInformation($"Job {job.Id} completed. Processed text: {processedText}");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Error processing job: {message}");
        }
        
        channel.BasicAck(deliveryTag: ea.DeliveryTag, multiple: false);
    };

    channel.BasicConsume(queue: queueName, autoAck: false, consumer: consumer);

    logger.LogInformation("Worker service started. Waiting for jobs...");
    logger.LogInformation("Press [enter] to exit.");
    Console.ReadLine();
}
catch (Exception ex)
{
    logger.LogError(ex, "Fatal error occurred");
    Environment.Exit(1);
}
finally
{
    channel?.Close();
    connection?.Close();
}

public class JobMessage
{
    public string Id { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}


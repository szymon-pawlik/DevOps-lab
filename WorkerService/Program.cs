using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WorkerService.Data;
using WorkerService.Models;
using System.Net.Http.Json;

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
var connectionString = configuration.GetConnectionString("DefaultConnection") 
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
var apiUrl = configuration["ApiUrl"] ?? "http://producer-api:8080";
var googleTranslateApiKey = configuration["GoogleTranslate:ApiKey"] ?? string.Empty;

// Setup HttpClient for SignalR notifications
var httpClient = new HttpClient
{
    BaseAddress = new Uri(apiUrl),
    Timeout = TimeSpan.FromSeconds(30)
};

// Setup HttpClient for Google Translate API
var translateHttpClient = new HttpClient
{
    Timeout = TimeSpan.FromSeconds(30)
};

// Setup DbContext
var services = new ServiceCollection();
services.AddDbContext<JobDbContext>(options =>
    options.UseNpgsql(connectionString));
var serviceProvider = services.BuildServiceProvider();


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

    // Define all queues for different job types (including priority queues)
    var queues = new[]
    {
        ("job_queue_uppercase", JobType.Uppercase),
        ("job_queue_uppercase_priority", JobType.Uppercase),
        ("job_queue_lowercase", JobType.Lowercase),
        ("job_queue_lowercase_priority", JobType.Lowercase),
        ("job_queue_reverse", JobType.Reverse),
        ("job_queue_reverse_priority", JobType.Reverse),
        ("job_queue_countwords", JobType.CountWords),
        ("job_queue_countwords_priority", JobType.CountWords),
        ("job_queue_translate", JobType.Translate),
        ("job_queue_translate_priority", JobType.Translate)
    };

    // Declare all queues and set up consumers
    foreach (var (queueName, jobType) in queues)
    {
        channel.QueueDeclare(queue: queueName, durable: false, exclusive: false, autoDelete: false, arguments: null);

        var consumer = new EventingBasicConsumer(channel);
        consumer.Received += async (model, ea) =>
        {
            var body = ea.Body.ToArray();
            var message = Encoding.UTF8.GetString(body);
            
            try
            {
                var jobMessage = JsonSerializer.Deserialize<JobMessage>(message);
                if (jobMessage != null && Guid.TryParse(jobMessage.Id, out var jobId))
                {
                    using var scope = serviceProvider.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<JobDbContext>();
                    
                    var job = await dbContext.Jobs.FindAsync(jobId);
                    if (job != null)
                    {
                        // Update status to Processing
                        job.Status = JobStatus.Processing;
                        await dbContext.SaveChangesAsync();
                        
                        // Notify via SignalR (through API)
                        try
                        {
                            var response = await httpClient.PostAsJsonAsync("/api/job/notify-update", new
                            {
                                Id = job.Id.ToString(),
                                Status = (int)JobStatus.Processing,
                                ProcessedText = (string?)null,
                                ProcessedAt = (DateTime?)null
                            });
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, $"Failed to send SignalR notification for processing status: {job.Id}");
                        }
                        
                        // Simulate processing work (less delay for high priority)
                        var delay = job.Priority == JobPriority.Critical ? 500 : 
                                   job.Priority == JobPriority.High ? 1000 : 2000;
                        await Task.Delay(delay);
                        
                        // Process text or file based on job type
                        string processedText;
                        byte[]? processedFileData = null;
                        string? processedFileName = null;
                        
                        if (job.FileData != null && !string.IsNullOrEmpty(job.OriginalFileName))
                        {
                            // Process file
                            var result = await ProcessFileAsync(job.FileData, job.OriginalFileName, jobType, translateHttpClient, googleTranslateApiKey, logger);
                            processedText = result.Text;
                            processedFileData = result.FileData;
                            processedFileName = result.FileName;
                        }
                        else
                        {
                            // Process text with real-time progress updates for all job types
                            processedText = await ProcessJobAsyncWithProgress(job.Text, jobType, translateHttpClient, googleTranslateApiKey, logger, job.Id, httpClient);
                        }
                        
                        // Update job in database
                        job.Status = JobStatus.Completed;
                        job.ProcessedText = processedText;
                        if (processedFileData != null)
                        {
                            job.ProcessedFileData = processedFileData;
                            job.ProcessedFileName = processedFileName;
                        }
                        job.ProcessedAt = DateTime.UtcNow;
                        await dbContext.SaveChangesAsync();
                        
                        // Notify via SignalR (through API)
                        try
                        {
                            var response = await httpClient.PostAsJsonAsync("/api/job/notify-update", new
                            {
                                Id = job.Id.ToString(),
                                Status = (int)JobStatus.Completed,
                                ProcessedText = processedText,
                                ProcessedAt = job.ProcessedAt
                            });
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, $"Failed to send SignalR notification for completed status: {job.Id}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"Error processing job: {message}");
                
                // Try to mark job as failed
                try
                {
                    var jobMessage = JsonSerializer.Deserialize<JobMessage>(message);
                    if (jobMessage != null && Guid.TryParse(jobMessage.Id, out var jobId))
                    {
                        using var scope = serviceProvider.CreateScope();
                        var dbContext = scope.ServiceProvider.GetRequiredService<JobDbContext>();
                        var job = await dbContext.Jobs.FindAsync(jobId);
                        if (job != null)
                        {
                            job.Status = JobStatus.Failed;
                            await dbContext.SaveChangesAsync();
                            
                            // Notify via SignalR (through API)
                            try
                            {
                                await httpClient.PostAsJsonAsync("/api/job/notify-update", new
                                {
                                    Id = job.Id.ToString(),
                                    Status = (int)JobStatus.Failed,
                                    ProcessedText = (string?)null,
                                    ProcessedAt = (DateTime?)null
                                });
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
            
            channel.BasicAck(deliveryTag: ea.DeliveryTag, multiple: false);
        };

        channel.BasicConsume(queue: queueName, autoAck: false, consumer: consumer);
    }

    
    // Keep the application running until cancellation
    var cancellationTokenSource = new CancellationTokenSource();
    Console.CancelKeyPress += (sender, e) =>
    {
        e.Cancel = true;
        cancellationTokenSource.Cancel();
    };
    
    // Wait for cancellation signal (SIGTERM/SIGINT)
    try
    {
        await Task.Delay(Timeout.Infinite, cancellationTokenSource.Token);
    }
    catch (OperationCanceledException)
    {
    }
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

// Helper methods and classes - must be after top-level statements
static async Task<string> ProcessJobAsync(string text, JobType jobType, HttpClient translateClient, string apiKey, ILogger logger)
{
    return jobType switch
    {
        JobType.Uppercase => text.ToUpper(),
        JobType.Lowercase => text.ToLower(),
        JobType.Reverse => new string(text.Reverse().ToArray()),
        JobType.CountWords => text.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Length.ToString(),
        JobType.Translate => await TranslateTextAsync(text, translateClient, apiKey, logger),
        _ => text
    };
}

static async Task<string> ProcessJobAsyncWithProgress(string text, JobType jobType, HttpClient translateClient, string apiKey, ILogger logger, Guid jobId, HttpClient httpClient)
{
    if (jobType == JobType.Translate)
    {
        return await TranslateTextAsyncWithProgress(text, translateClient, apiKey, logger, jobId, httpClient);
    }
    
    // For other job types, process with real-time progress
    return await ProcessTextWithProgress(text, jobType, logger, jobId, httpClient);
}

static async Task<string> ProcessTextWithProgress(string text, JobType jobType, ILogger logger, Guid jobId, HttpClient httpClient)
{
    if (string.IsNullOrWhiteSpace(text))
    {
        return text;
    }

    var result = new StringBuilder();
    var accumulatedText = "";

    switch (jobType)
    {
        case JobType.Reverse:
            // For reverse, process from the end character by character
            for (int i = text.Length - 1; i >= 0; i--)
            {
                var charToAdd = text[i];
                result.Append(charToAdd);
                accumulatedText = result.ToString();

                // Send progress update for every character
                try
                {
                    await httpClient.PostAsJsonAsync("/api/job/notify-update", new
                    {
                        Id = jobId.ToString(),
                        Status = (int)JobStatus.Processing,
                        ProcessedText = accumulatedText + (i > 0 ? "..." : ""),
                        ProcessedAt = (DateTime?)null
                    });
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, $"Failed to send progress update for job {jobId}");
                }
                
                // Small delay for typing effect
                await Task.Delay(30);
            }
            return result.ToString();

        case JobType.CountWords:
            // For count words, show progress by counting words processed so far
            var words = text.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < words.Length; i++)
            {
                var countSoFar = i + 1;
                accumulatedText = countSoFar.ToString();

                // Send progress update
                try
                {
                    await httpClient.PostAsJsonAsync("/api/job/notify-update", new
                    {
                        Id = jobId.ToString(),
                        Status = (int)JobStatus.Processing,
                        ProcessedText = accumulatedText + (i < words.Length - 1 ? "..." : ""),
                        ProcessedAt = (DateTime?)null
                    });
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, $"Failed to send progress update for job {jobId}");
                }
                await Task.Delay(50);
            }
            return words.Length.ToString();

        case JobType.Uppercase:
        case JobType.Lowercase:
        default:
            // For uppercase and lowercase, process character by character for typing effect
            for (int i = 0; i < text.Length; i++)
            {
                var charToProcess = text[i];
                char processedChar = jobType == JobType.Uppercase ? char.ToUpper(charToProcess) : char.ToLower(charToProcess);
                
                result.Append(processedChar);
                accumulatedText = result.ToString();

                // Send progress update for every character
                try
                {
                    await httpClient.PostAsJsonAsync("/api/job/notify-update", new
                    {
                        Id = jobId.ToString(),
                        Status = (int)JobStatus.Processing,
                        ProcessedText = accumulatedText + (i < text.Length - 1 ? "..." : ""),
                        ProcessedAt = (DateTime?)null
                    });
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, $"Failed to send progress update for job {jobId}");
                }

                // Small delay for typing effect (faster for regular chars, slower for spaces)
                var delay = char.IsWhiteSpace(charToProcess) ? 50 : 30;
                await Task.Delay(delay);
            }
            return result.ToString();
    }
}

static async Task<string> TranslateTextAsync(string text, HttpClient translateClient, string apiKey, ILogger logger)
{
    if (string.IsNullOrWhiteSpace(text))
    {
        return text;
    }

    // Use free Google Translate (translate.google.com) - works without API key
    // Similar to @vitalets/google-translate-api for Node.js
    try
    {
        return await TranslateTextFreeAsync(text, translateClient, logger);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Free Google Translate failed, using fallback");
        return TranslateTextFallback(text);
    }
}

static async Task<string> TranslateTextAsyncWithProgress(string text, HttpClient translateClient, string apiKey, ILogger logger, Guid jobId, HttpClient httpClient)
{
    if (string.IsNullOrWhiteSpace(text))
    {
        return text;
    }

    try
    {
        // Split text into sentences for progressive translation
        // First try simple split by sentence endings
        var sentences = new List<string>();
        var currentSentence = new StringBuilder();
        
        for (int i = 0; i < text.Length; i++)
        {
            currentSentence.Append(text[i]);
            
            // Check if we hit a sentence ending
            if (text[i] == '.' || text[i] == '!' || text[i] == '?')
            {
                // Check if next char is space or end of text
                if (i == text.Length - 1 || char.IsWhiteSpace(text[i + 1]))
                {
                    var sentence = currentSentence.ToString().Trim();
                    if (!string.IsNullOrWhiteSpace(sentence))
                    {
                        sentences.Add(sentence);
                    }
                    currentSentence.Clear();
                }
            }
        }
        
        // Add remaining text if any
        if (currentSentence.Length > 0)
        {
            var sentence = currentSentence.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(sentence))
            {
                sentences.Add(sentence);
            }
        }
        

        // If no sentences found, try splitting by paragraphs or commas
        if (sentences.Count == 0)
        {
            // Try splitting by paragraphs first
            var paragraphs = text.Split(new[] { "\n\n", "\r\n\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            if (paragraphs.Length > 1)
            {
                return await TranslateParagraphsProgressively(paragraphs, translateClient, logger, jobId, httpClient);
            }
            
            // Try splitting by commas for very long text
            if (text.Length > 500)
            {
                var parts = text.Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1)
                {
                    return await TranslatePartsProgressively(parts, translateClient, logger, jobId, httpClient, ", ");
                }
            }
            
            // If still no good split, try splitting by words
            var words = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 10)
            {
                return await TranslateWordsProgressively(words, translateClient, logger, jobId, httpClient);
            }
            
            // Last resort: translate whole text
            return await TranslateTextFreeAsync(text, translateClient, logger);
        }

        var translatedParts = new List<string>();
        var accumulatedText = "";

        for (int i = 0; i < sentences.Count; i++)
        {
            var sentence = sentences[i];
            if (string.IsNullOrWhiteSpace(sentence)) continue;

            try
            {
                string translated;
                
                // If sentence is too long (over 5000 chars), split it further
                if (sentence.Length > 5000)
                {
                    var subParts = sentence.Split(new[] { ", ", "; " }, StringSplitOptions.RemoveEmptyEntries);
                    if (subParts.Length > 1)
                    {
                        var subTranslated = await TranslatePartsProgressively(subParts, translateClient, logger, jobId, httpClient, ", ");
                        translated = subTranslated;
                    }
                    else
                    {
                        // If still too long, split by words
                        var words = sentence.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        translated = await TranslateWordsProgressively(words, translateClient, logger, jobId, httpClient);
                    }
                }
                else
                {
                    // Translate sentence normally
                    translated = await TranslateTextFreeAsync(sentence, translateClient, logger);
                }
                
                // Remove the prefix if it's added by TranslateTextFreeAsync (it adds [Tłumaczone z...] only once)
                if (i > 0 && translated.StartsWith("[Tłumaczone z"))
                {
                    var prefixEnd = translated.IndexOf("] ");
                    if (prefixEnd > 0)
                    {
                        translated = translated.Substring(prefixEnd + 2);
                    }
                }
                
                translatedParts.Add(translated);
                
                // Display translated sentence character by character for typing effect
                for (int j = 0; j < translated.Length; j++)
                {
                    accumulatedText += translated[j];
                    var suffix = (i < sentences.Count - 1 && j == translated.Length - 1) ? "..." : (j < translated.Length - 1 ? "..." : "");

                    // Send progress update for every character
                    try
                    {
                        await httpClient.PostAsJsonAsync("/api/job/notify-update", new
                        {
                            Id = jobId.ToString(),
                            Status = (int)JobStatus.Processing,
                            ProcessedText = accumulatedText + suffix,
                            ProcessedAt = (DateTime?)null
                        });
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, $"Failed to send progress update for job {jobId}");
                    }

                    // Small delay for typing effect
                    var delay = char.IsWhiteSpace(translated[j]) ? 50 : 30;
                    await Task.Delay(delay);
                }
                
                // Add space after sentence if not last
                if (i < sentences.Count - 1)
                {
                    accumulatedText += " ";
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"Failed to translate sentence {i + 1}/{sentences.Count} for job {jobId}, using original. Error: {ex.Message}");
                translatedParts.Add(sentence);
                accumulatedText += (accumulatedText.Length > 0 ? " " : "") + sentence;
                
                // Continue with next sentence even if this one failed
                continue;
            }
        }

        if (translatedParts.Count == 0)
        {
            logger.LogWarning($"No sentences were translated for job {jobId}, falling back to standard translation");
            return await TranslateTextAsync(text, translateClient, apiKey, logger);
        }

        var finalResult = string.Join(" ", translatedParts);
        
        // Final update to remove "..." if still present
        try
        {
            await httpClient.PostAsJsonAsync("/api/job/notify-update", new
            {
                Id = jobId.ToString(),
                Status = (int)JobStatus.Processing,
                ProcessedText = finalResult,
                ProcessedAt = (DateTime?)null
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, $"Failed to send final progress update for job {jobId}");
        }
        
        return finalResult;
    }
    catch (Exception ex)
    {
        logger.LogError(ex, $"Progressive translation failed for job {jobId}, using standard translation. Error: {ex.Message}, StackTrace: {ex.StackTrace}");
        return await TranslateTextAsync(text, translateClient, apiKey, logger);
    }
}

static async Task<string> TranslateParagraphsProgressively(string[] paragraphs, HttpClient translateClient, ILogger logger, Guid jobId, HttpClient httpClient)
{
    var translatedParts = new List<string>();
    var accumulatedText = "";

    for (int i = 0; i < paragraphs.Length; i++)
    {
        var paragraph = paragraphs[i].Trim();
        if (string.IsNullOrWhiteSpace(paragraph)) continue;

        try
        {
            var translated = await TranslateTextFreeAsync(paragraph, translateClient, logger);
            
            // Remove prefix after first paragraph
            if (i > 0 && translated.StartsWith("[Tłumaczone z"))
            {
                var prefixEnd = translated.IndexOf("] ");
                if (prefixEnd > 0)
                {
                    translated = translated.Substring(prefixEnd + 2);
                }
            }
            
            translatedParts.Add(translated);
            
            // Display translated paragraph character by character for typing effect
            var separator = (accumulatedText.Length > 0 ? "\n\n" : "");
            accumulatedText += separator;
            
            for (int j = 0; j < translated.Length; j++)
            {
                accumulatedText += translated[j];
                var suffix = (i < paragraphs.Length - 1 && j == translated.Length - 1) ? "..." : (j < translated.Length - 1 ? "..." : "");

                // Send progress update for every character
                try
                {
                    await httpClient.PostAsJsonAsync("/api/job/notify-update", new
                    {
                        Id = jobId.ToString(),
                        Status = (int)JobStatus.Processing,
                        ProcessedText = accumulatedText + suffix,
                        ProcessedAt = (DateTime?)null
                    });
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, $"Failed to send progress update for job {jobId}");
                }

                // Small delay for typing effect
                var delay = char.IsWhiteSpace(translated[j]) ? 50 : 30;
                await Task.Delay(delay);
            }
            
            // Add paragraph separator if not last
            if (i < paragraphs.Length - 1)
            {
                accumulatedText += "\n\n";
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, $"Failed to translate paragraph {i + 1}, using original");
            translatedParts.Add(paragraph);
            accumulatedText += (accumulatedText.Length > 0 ? "\n\n" : "") + paragraph;
        }
    }

    return string.Join("\n\n", translatedParts);
}

static async Task<string> TranslatePartsProgressively(string[] parts, HttpClient translateClient, ILogger logger, Guid jobId, HttpClient httpClient, string separator)
{
    var translatedParts = new List<string>();
    var accumulatedText = "";

    for (int i = 0; i < parts.Length; i++)
    {
        var part = parts[i].Trim();
        if (string.IsNullOrWhiteSpace(part)) continue;

        try
        {
            var translated = await TranslateTextFreeAsync(part, translateClient, logger);
            
            // Remove prefix after first part
            if (i > 0 && translated.StartsWith("[Tłumaczone z"))
            {
                var prefixEnd = translated.IndexOf("] ");
                if (prefixEnd > 0)
                {
                    translated = translated.Substring(prefixEnd + 2);
                }
            }
            
            translatedParts.Add(translated);
            
            // Display translated part character by character for typing effect
            var partSeparator = (accumulatedText.Length > 0 ? separator : "");
            accumulatedText += partSeparator;
            
            for (int j = 0; j < translated.Length; j++)
            {
                accumulatedText += translated[j];
                var suffix = (i < parts.Length - 1 && j == translated.Length - 1) ? "..." : (j < translated.Length - 1 ? "..." : "");

                // Send progress update for every character
                try
                {
                    await httpClient.PostAsJsonAsync("/api/job/notify-update", new
                    {
                        Id = jobId.ToString(),
                        Status = (int)JobStatus.Processing,
                        ProcessedText = accumulatedText + suffix,
                        ProcessedAt = (DateTime?)null
                    });
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, $"Failed to send progress update for job {jobId}");
                }

                // Small delay for typing effect
                var delay = char.IsWhiteSpace(translated[j]) ? 50 : 30;
                await Task.Delay(delay);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, $"Failed to translate part {i + 1}, using original");
            translatedParts.Add(part);
            accumulatedText += (accumulatedText.Length > 0 ? separator : "") + part;
        }
    }

    return string.Join(separator, translatedParts);
}

static async Task<string> TranslateWordsProgressively(string[] words, HttpClient translateClient, ILogger logger, Guid jobId, HttpClient httpClient)
{
    var translatedParts = new List<string>();
    var accumulatedText = "";
    var batchSize = 5; // Translate 5 words at a time

    for (int i = 0; i < words.Length; i += batchSize)
    {
        var batch = words.Skip(i).Take(batchSize).ToArray();
        var batchText = string.Join(" ", batch);

        try
        {
            var translated = await TranslateTextFreeAsync(batchText, translateClient, logger);
            translatedParts.Add(translated);
            
            // Display translated batch character by character for typing effect
            var batchSeparator = (accumulatedText.Length > 0 ? " " : "");
            accumulatedText += batchSeparator;
            
            for (int j = 0; j < translated.Length; j++)
            {
                accumulatedText += translated[j];
                var suffix = (i + batchSize < words.Length && j == translated.Length - 1) ? "..." : (j < translated.Length - 1 ? "..." : "");

                // Send progress update for every character
                try
                {
                    await httpClient.PostAsJsonAsync("/api/job/notify-update", new
                    {
                        Id = jobId.ToString(),
                        Status = (int)JobStatus.Processing,
                        ProcessedText = accumulatedText + suffix,
                        ProcessedAt = (DateTime?)null
                    });
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, $"Failed to send progress update for job {jobId}");
                }

                // Small delay for typing effect
                var delay = char.IsWhiteSpace(translated[j]) ? 50 : 30;
                await Task.Delay(delay);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, $"Failed to translate batch {i}, using original");
            translatedParts.Add(batchText);
            accumulatedText += (accumulatedText.Length > 0 ? " " : "") + batchText;
        }
    }

    return string.Join(" ", translatedParts);
}

static async Task<string> TranslateTextFreeAsync(string text, HttpClient translateClient, ILogger logger)
{
    try
    {
        // Use free Google Translate (translate.google.com) - no API key needed
        // This is similar to @vitalets/google-translate-api library
        var baseUrl = "https://translate.googleapis.com/translate_a/single";
        
        // Determine target language - try to detect if text is Polish or English
        bool likelyPolish = text.Any(c => "ąćęłńóśźżĄĆĘŁŃÓŚŹŻ".Contains(c));
        string targetLang = likelyPolish ? "en" : "pl";
        string sourceLang = likelyPolish ? "pl" : "en";

        // Build request URL
        var url = $"{baseUrl}?client=gtx&sl={sourceLang}&tl={targetLang}&dt=t&q={Uri.EscapeDataString(text)}";
        
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
        request.Headers.Add("Accept", "application/json");

        var response = await translateClient.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            // If rate limited (429), try with auto-detection
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                logger.LogWarning("Rate limited, trying with auto-detection");
                url = $"{baseUrl}?client=gtx&sl=auto&tl={targetLang}&dt=t&q={Uri.EscapeDataString(text)}";
                request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
                response = await translateClient.SendAsync(request);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"Translation failed: {response.StatusCode}");
            }
        }

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonSerializer.Deserialize<JsonElement>(content);
        
        // Extract translated text from response
        // Response format: [[["translated text",...],...],...]
        string translatedText = text;
        try
        {
            if (json.ValueKind == JsonValueKind.Array && json.GetArrayLength() > 0)
            {
                var firstArray = json[0];
                if (firstArray.ValueKind == JsonValueKind.Array && firstArray.GetArrayLength() > 0)
                {
                    var secondArray = firstArray[0];
                    if (secondArray.ValueKind == JsonValueKind.Array && secondArray.GetArrayLength() > 0)
                    {
                        translatedText = secondArray[0].GetString() ?? text;
                    }
                }
            }
        }
        catch (Exception parseEx)
        {
            logger.LogWarning(parseEx, "Failed to parse translation response, using original text");
            translatedText = text;
        }

        // Try to get detected source language from response
        try
        {
            if (json.ValueKind == JsonValueKind.Array && json.GetArrayLength() > 2)
            {
                var detectedLang = json[2].GetString();
                if (!string.IsNullOrEmpty(detectedLang))
                {
                    sourceLang = detectedLang;
                    targetLang = (detectedLang == "pl") ? "en" : "pl";
                }
            }
        }
        catch
        {
            // Keep original source/target languages
        }

        return $"[Tłumaczone z {sourceLang.ToUpper()} na {targetLang.ToUpper()}] {translatedText}";
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error using free Google Translate");
        throw; // Re-throw to trigger fallback
    }
}

static string TranslateTextFallback(string text)
{
    // Fallback: Simple bidirectional translation dictionary (EN <-> PL)
    var enToPl = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "hello", "cześć" },
        { "world", "świat" },
        { "test", "test" },
        { "job", "zadanie" },
        { "text", "tekst" },
        { "processing", "przetwarzanie" },
        { "completed", "ukończone" },
        { "pending", "oczekujące" },
        { "failed", "nieudane" },
        { "the", "ten" },
        { "is", "jest" },
        { "a", "a" },
        { "an", "an" },
        { "and", "i" },
        { "or", "lub" },
        { "to", "do" },
        { "from", "z" },
        { "with", "z" },
        { "for", "dla" }
    };

    // Create reverse dictionary (PL -> EN)
    var plToEn = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var kvp in enToPl)
    {
        if (!plToEn.ContainsKey(kvp.Value))
        {
            plToEn[kvp.Value] = kvp.Key;
        }
    }

    // Detect language by checking if text contains Polish characters or known Polish words
    bool isPolish = text.Any(c => "ąćęłńóśźż".Contains(c, StringComparison.OrdinalIgnoreCase)) ||
                   text.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                       .Any(word => plToEn.ContainsKey(word.ToLower()));

    var dictionary = isPolish ? plToEn : enToPl;
    var sourceLang = isPolish ? "PL" : "EN";
    var targetLang = isPolish ? "EN" : "PL";

    var words = text.Split(new[] { ' ', '\t', '\n', '\r', '.', ',', '!', '?', ';', ':' }, 
        StringSplitOptions.RemoveEmptyEntries);
    
    var translatedWords = new List<string>();
    var untranslatedWords = new List<string>();
    
    foreach (var word in words)
    {
        var cleanWord = word.Trim().ToLower();
        if (dictionary.TryGetValue(cleanWord, out var translation))
        {
            // Preserve original case
            if (word.Length > 0 && char.IsUpper(word[0]))
                translatedWords.Add(char.ToUpper(translation[0]) + translation.Substring(1));
            else
                translatedWords.Add(translation);
        }
        else
        {
            translatedWords.Add($"[UNTRANSLATED:{word}]");
            untranslatedWords.Add(word);
        }
    }

    var result = string.Join(" ", translatedWords);
    
    // Add information about untranslated words
    if (untranslatedWords.Any())
    {
        var untranslatedList = string.Join(", ", untranslatedWords.Distinct());
        return $"[Tłumaczone z {sourceLang} na {targetLang}] {result} | [Brak możliwości tłumaczenia: {untranslatedList}]";
    }
    
    // If no translation happened, add a prefix to show it was processed
    if (result == text)
    {
        return $"[Tłumaczenie {sourceLang}->{targetLang}] {text}";
    }
    
    return $"[Tłumaczone z {sourceLang} na {targetLang}] {result}";
}

static async Task<FileProcessResult> ProcessFileAsync(byte[] fileData, string fileName, JobType jobType, HttpClient translateClient, string apiKey, ILogger logger)
{
    // For text files, process the content
    if (fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
    {
        var text = Encoding.UTF8.GetString(fileData);
        var processedText = await ProcessJobAsync(text, jobType, translateClient, apiKey, logger);
        var processedBytes = Encoding.UTF8.GetBytes(processedText);
        
        return new FileProcessResult
        {
            Text = processedText,
            FileData = processedBytes,
            FileName = $"processed_{fileName}"
        };
    }
    
    // For other files, just return text representation
    return new FileProcessResult
    {
        Text = $"File processed: {fileName}",
        FileData = fileData, // Return original for now
        FileName = $"processed_{fileName}"
    };
}

public class JobMessage
{
    public string Id { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public JobType Type { get; set; }
    public JobPriority Priority { get; set; } = JobPriority.Normal;
    public DateTime CreatedAt { get; set; }
    public string UserId { get; set; } = string.Empty;
    public bool HasFile { get; set; }
    public string? FileName { get; set; }
}

public class FileProcessResult
{
    public string Text { get; set; } = string.Empty;
    public byte[]? FileData { get; set; }
    public string? FileName { get; set; }
}


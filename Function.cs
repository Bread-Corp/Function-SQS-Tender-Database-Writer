using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tender_AI_Tagging_Lambda.Services;
using TenderDatabaseWriterLambda.Data;
using TenderDatabaseWriterLambda.Interfaces;
using TenderDatabaseWriterLambda.Models.Input;
using TenderDatabaseWriterLambda.Services;


// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace TenderDatabaseWriterLambda;

/// <summary>
/// AWS Lambda function to process enriched tender messages from the 'WriteQueue'
/// and save them to the SQL Server database using Entity Framework Core.
/// </summary>
public class Function
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ISqsService _sqsService;
    private readonly IMessageFactory _messageFactory;
    private readonly ILogger<Function> _logger;
    private readonly IAmazonSQS _sqsClient;

    // Queue URLs from environment variables
    private readonly string _sourceQueueUrl; // WriteQueue.fifo
    private readonly string _failedQueueUrl; // DBWriteFailedQueue.fifo

    // JSON options for serializing failure messages
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Default constructor for Lambda runtime with DI setup.
    /// </summary>
    public Function()
    {
        // Configure DI
        _serviceProvider = ConfigureServices();

        // Resolve top-level services
        _sqsService = _serviceProvider.GetRequiredService<ISqsService>();
        _messageFactory = _serviceProvider.GetRequiredService<IMessageFactory>();
        _logger = _serviceProvider.GetRequiredService<ILogger<Function>>();
        _sqsClient = _serviceProvider.GetRequiredService<IAmazonSQS>();

        // Load and validate environment variables
        _sourceQueueUrl = Environment.GetEnvironmentVariable("SOURCE_QUEUE_URL") ?? throw new InvalidOperationException("SOURCE_QUEUE_URL (WriteQueue) is required.");
        _failedQueueUrl = Environment.GetEnvironmentVariable("FAILED_QUEUE_URL") ?? throw new InvalidOperationException("FAILED_QUEUE_URL (dbWriteFailedQueue) is required.");

        _logger.LogInformation("DB Writer Lambda initialized. Source: {Source}, Failed: {Failed}",
            !string.IsNullOrEmpty(_sourceQueueUrl), !string.IsNullOrEmpty(_failedQueueUrl));
    }

    /// <summary>
    /// Configures the dependency injection container.
    /// </summary>
    private static ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        services.AddLogging(builder =>
        {
            builder.AddJsonConsole(options =>
            {
                options.IncludeScopes = false;
                options.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fffZ";
                options.JsonWriterOptions = new JsonWriterOptions { Indented = false };
            });
            builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Information);
        });

        // Register AWS Service Clients (Singleton)
        services.AddSingleton<IAmazonSQS, AmazonSQSClient>();
        // Note: We don't need Bedrock or SSM in this function

        // Register Application Services
        services.AddTransient<ISqsService, SqsService>();
        services.AddTransient<IMessageFactory, MessageFactory>();
        // ITenderWriterService is transient because it's created for each batch
        services.AddTransient<ITenderWriterService, TenderWriterService>();

        // Register the DbContextFactory as a Singleton
        var connectionString = Environment.GetEnvironmentVariable("DB_CONNECTION_STRING") ?? throw new InvalidOperationException("DB_CONNECTION_STRING is required.");
        services.AddDbContextFactory<ApplicationDbContext>(options =>
            options.UseSqlServer(connectionString));

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Main Lambda handler with continuous polling.
    /// </summary>
    public async Task<string> FunctionHandler(SQSEvent evnt, ILambdaContext context)
    {
        var functionStart = DateTime.UtcNow;
        var totalProcessed = 0;
        var totalFailed = 0;
        var totalDeleted = 0;
        var batchCount = 0;

        _logger.LogInformation("DB Writer invocation started - RequestId: {RequestId}, InitialEventMessages: {InitialMessageCount}",
            context.AwsRequestId, evnt.Records?.Count ?? 0);

        try
        {
            // Process initial batch from trigger
            if (evnt?.Records?.Any() == true)
            {
                batchCount++;
                _logger.LogInformation("Processing initial SQS event batch #{BatchNumber} - MessageCount: {MessageCount}", batchCount, evnt.Records.Count);
                var initialMessages = evnt.Records.Select(ConvertSqsEventToMessage).ToList();
                var initialResult = await ProcessMessageBatch(initialMessages, batchCount);
                totalProcessed += initialResult.processed;
                totalFailed += initialResult.failed;
                totalDeleted += initialResult.deleted;
            }

            // Continuous polling loop
            while (context.RemainingTime > TimeSpan.FromSeconds(30)) // Safety margin
            {
                var messages = await PollMessagesFromQueue(10); // Max SQS batch size
                if (!messages.Any())
                {
                    _logger.LogInformation("Queue polling complete. No more messages found.");
                    break;
                }

                batchCount++;
                var batchResult = await ProcessMessageBatch(messages, batchCount);
                totalProcessed += batchResult.processed;
                totalFailed += batchResult.failed;
                totalDeleted += batchResult.deleted;

                await Task.Delay(100); // Small delay
            }

            var totalDuration = (DateTime.UtcNow - functionStart).TotalMilliseconds;
            var result = $"Success. Batches: {batchCount}, Processed: {totalProcessed}, Failed: {totalFailed}, Deleted: {totalDeleted}, Duration: {totalDuration:F0}ms";
            _logger.LogInformation("Lambda execution finished. {Result}", result);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lambda execution failed unexpectedly. Processed: {TotalProcessed}, Failed: {TotalFailed}", totalProcessed, totalFailed);
            throw; // Re-throw to indicate failure to Lambda runtime
        }
    }

    /// <summary>
    /// Polls messages directly from the source queue (WriteQueue).
    /// </summary>
    private async Task<List<QueueMessage>> PollMessagesFromQueue(int maxMessages)
    {
        try
        {
            var request = new ReceiveMessageRequest
            {
                QueueUrl = _sourceQueueUrl,
                MaxNumberOfMessages = maxMessages,
                WaitTimeSeconds = 2,
                VisibilityTimeout = 300, // 5 minutes processing time
                MessageSystemAttributeNames = new List<string> { "All" },
                MessageAttributeNames = new List<string> { "All" }
            };
            var response = await _sqsClient.ReceiveMessageAsync(request);
            _logger.LogDebug("Polled {MessageCount} messages from source queue.", response.Messages?.Count ?? 0);
            return response.Messages?.Select(ConvertSqsApiMessageToQueueMessage).ToList() ?? new List<QueueMessage>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to poll messages from source queue: {QueueUrl}", _sourceQueueUrl);
            return new List<QueueMessage>();
        }
    }

    /// <summary>
    /// Processes a batch of SQS messages: deserialize, map, write to DB, route failures, delete.
    /// </summary>
    private async Task<(int processed, int failed, int deleted)> ProcessMessageBatch(List<QueueMessage> messages, int batchNumber)
    {
        var batchStart = DateTime.UtcNow;
        var successReceipts = new List<(string id, string receiptHandle)>();
        var failedMessages = new List<(string originalBody, string messageGroupId, Exception exception, QueueMessage record)>();

        _logger.LogInformation(
            "Starting Batch Processing. BatchNumber: {BatchNumber}, TenderSource: {TenderSource}, MessageCount: {MessageCount}",
            batchNumber,
            messages.FirstOrDefault()?.MessageGroupId ?? "Unknown",
            messages.Count
        );

        foreach (var message in messages)
        {
            try
            {
                if (string.IsNullOrEmpty(message.Body)) throw new InvalidOperationException("Message body is null or empty.");

                // 1. Deserialize
                TenderMessageBase? tenderMessage = _messageFactory.CreateMessage(message.Body, message.MessageGroupId);
                if (tenderMessage == null) throw new InvalidOperationException($"Message factory returned null for GroupId: {message.MessageGroupId}");

                // 2. Get a new ITenderWriterService from the DI container
                using var scope = _serviceProvider.CreateScope();
                var tenderWriterService = scope.ServiceProvider.GetRequiredService<ITenderWriterService>();

                // 3. Transform and Write to DB
                await tenderWriterService.WriteTenderAsync(tenderMessage);

                // 4. Mark as successful FOR DELETION
                successReceipts.Add((message.MessageId, message.ReceiptHandle));
                _logger.LogDebug("Successfully processed message {MessageId} for tender {TenderNumber}", message.MessageId, tenderMessage.TenderNumber);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Message processing failed - MessageId: {MessageId}, GroupId: {MessageGroupId}", message.MessageId, message.MessageGroupId);
                failedMessages.Add((message.Body, message.MessageGroupId, ex, message));
            }
        }

        // --- Phase 2: Routing Failures ---
        var messagesToDelete = new List<(string id, string receiptHandle)>(successReceipts);

        if (failedMessages.Any())
        {
            try
            {
                var dlqMessages = failedMessages.Select(f => new
                {
                    OriginalMessageBody = f.originalBody,
                    MessageGroupId = f.messageGroupId,
                    ErrorMessage = f.exception.Message,
                    ErrorType = f.exception.GetType().Name,
                    StackTrace = f.exception.StackTrace,
                    ProcessedBy = "Sqs_Database_Writer",
                    Timestamp = DateTime.UtcNow
                }).Cast<object>().ToList();

                await _sqsService.SendMessageBatchAsync(_failedQueueUrl, dlqMessages);
                // Add failed messages to the delete list (they were handled)
                messagesToDelete.AddRange(failedMessages.Select(fm => (fm.record.MessageId, fm.record.ReceiptHandle)));
                _logger.LogInformation("Successfully sent {Count} failed messages to dbWriteFailedQueue.", failedMessages.Count);
            }
            catch (Exception dlqEx)
            {
                _logger.LogCritical(dlqEx, "CRITICAL: Failed to send {Count} messages to dbWriteFailedQueue. These messages will be retried by SQS.", failedMessages.Count);
                // DO NOT add them to the delete list. Let them retry.
            }
        }

        // --- Phase 3: Deletion ---
        var deletedCount = 0;
        if (messagesToDelete.Any())
        {
            try
            {
                await _sqsService.DeleteMessageBatchAsync(_sourceQueueUrl, messagesToDelete);
                deletedCount = messagesToDelete.Count;
                _logger.LogInformation("Successfully deleted {Count} messages from source queue (WriteQueue).", deletedCount);
            }
            catch (Exception deleteEx)
            {
                _logger.LogError(deleteEx, "Failed to delete {Count} messages from source queue (WriteQueue). They will likely be reprocessed.", messagesToDelete.Count);
            }
        }

        var batchDuration = (DateTime.UtcNow - batchStart).TotalMilliseconds;
        _logger.LogInformation("Batch {BatchNumber} processing complete. Success: {ProcessedCount}, Failed: {FailedCount}, Deleted: {DeletedCount}, Duration: {Duration}ms",
            batchNumber, successReceipts.Count, failedMessages.Count, deletedCount, batchDuration);

        return (successReceipts.Count, failedMessages.Count, deletedCount);
    }

    // --- SQS Message Conversion Helpers ---
    private QueueMessage ConvertSqsEventToMessage(SQSEvent.SQSMessage record)
    {
        return new QueueMessage
        {
            MessageId = record.MessageId,
            Body = record.Body,
            ReceiptHandle = record.ReceiptHandle,
            MessageGroupId = GetMessageGroupId(record),
            Attributes = record.Attributes,
            MessageAttributes = ConvertMessageAttributes(record.MessageAttributes)
        };
    }

    private QueueMessage ConvertSqsApiMessageToQueueMessage(Message msg)
    {
        return new QueueMessage
        {
            MessageId = msg.MessageId,
            Body = msg.Body,
            ReceiptHandle = msg.ReceiptHandle,
            MessageGroupId = GetMessageGroupIdFromSqsMessage(msg),
            Attributes = msg.Attributes,
            MessageAttributes = msg.MessageAttributes
        };
    }

    private static string GetMessageGroupId(SQSEvent.SQSMessage record)
    {
        if (record.Attributes?.TryGetValue("MessageGroupId", out var groupId) == true) return groupId;
        return "UnknownGroup";
    }

    private static string GetMessageGroupIdFromSqsMessage(Message message)
    {
        if (message.Attributes?.TryGetValue("MessageGroupId", out var groupId) == true) return groupId;
        return "UnknownGroup";
    }

    private Dictionary<string, MessageAttributeValue>? ConvertMessageAttributes(Dictionary<string, SQSEvent.MessageAttribute> eventAttributes)
    {
        if (eventAttributes == null) return null;
        return eventAttributes.ToDictionary(
            kvp => kvp.Key,
            kvp => new MessageAttributeValue
            {
                StringValue = kvp.Value.StringValue,
                BinaryValue = kvp.Value.BinaryValue,
                DataType = kvp.Value.DataType
            }
        );
    }
}
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Dapper;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Npgsql;
using SendGrid;
using SendGrid.Helpers.Mail;

namespace EventManagementFunctionApp
{
    public class ServiceBusQueueTrigger
    {
        private readonly ILogger<ServiceBusQueueTrigger> _logger;
        private readonly string? _connectionString;
        private readonly GraphServiceClient _graphClient;
        private readonly string? _fromEmail;
        private readonly string? _sendGridApiKey;

        public ServiceBusQueueTrigger(ILogger<ServiceBusQueueTrigger> logger, IConfiguration configuration)
        {
            _logger = logger;
            _connectionString = configuration["DBConnectionString"];

            var clientId = configuration["GraphClientId"];
            var tenantId = configuration["GraphTenantId"];
            var clientSecret = configuration["GraphClientSecret"];

            _sendGridApiKey = configuration["SendGridApiKey"];
            _fromEmail = configuration["FromEmail"];

            var clientSecretCredential = new ClientSecretCredential(tenantId, clientId, clientSecret);
            _graphClient = new GraphServiceClient(clientSecretCredential);

        }

        [Function(nameof(ServiceBusQueueTrigger))]
        public async Task Run(
            [ServiceBusTrigger("appqueue", Connection = "eventmanagement2024_SERVICEBUS")]
            ServiceBusReceivedMessage message,
            ServiceBusMessageActions messageActions)
        {
            _logger.LogInformation("Message ID: {MessageId}, Body: {Body}, Content-Type: {ContentType}", message.MessageId, message.Body, message.ContentType);
            var registrationDto = System.Text.Json.JsonSerializer.Deserialize<EventRegistrationDto>(message.Body.ToString());

            try
            {
                if (registrationDto?.Action == "Register")
                {
                    await RegisterEventAsync(registrationDto.EventId.ToString()!, registrationDto.UserId!);
                    var userEmail = await GetUserPrincipalNameAsync(registrationDto.UserId!);
                    await SendEmailAsync(userEmail, "Registration Confirmation", "You have successfully registered for the event.");
                    _logger.LogInformation("Action: Register, UserId: {UserId}, EventId: {EventId}", registrationDto.UserId, registrationDto.EventId);
                }
                else if (registrationDto?.Action == "Unregister")
                {
                    await UnregisterEventAsync(registrationDto.EventId.ToString()!, registrationDto.UserId!);
                    var userEmail = await GetUserPrincipalNameAsync(registrationDto.UserId!);
                    await SendEmailAsync(userEmail, "Unregistration Confirmation", "You have successfully unregistered from the event.");
                    _logger.LogInformation("Action: Unregister, UserId: {UserId}, EventId: {EventId}", registrationDto.UserId, registrationDto.EventId);
                }

                await messageActions.CompleteMessageAsync(message);
            }
            catch (Exception ex)
            {
                _logger.LogError("Error processing message: {ErrorMessage}", ex.Message);
            }
        }

        private async Task RegisterEventAsync(string eventId, string userId)
        {
            using (var connection = new NpgsqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                using (var transaction = connection.BeginTransaction())
                {
                    try
                    {
                        var eventGuid = Guid.Parse(eventId);
                        var eventQuery = "SELECT * FROM \"Events\" WHERE \"Id\" = @EventId FOR UPDATE";
                        var eventDetails = await connection.QuerySingleOrDefaultAsync(eventQuery, new { EventId = eventGuid }, transaction) ?? throw new Exception("Event not found.");
                        var parameters = new { EventId = eventGuid, UserId = userId };
                        var insertRegistrationQuery = "INSERT INTO \"EventRegistrations\" (\"EventId\", \"UserId\", \"Action\") VALUES (@EventId, @UserId, 'Register')";

                        await connection.ExecuteAsync(insertRegistrationQuery, parameters, transaction);

                        transaction.Commit();
                    }
                    catch (Exception ex)
                    {
                        transaction.Rollback();
                        _logger.LogError("Error during registration: {ErrorMessage}", ex.Message);
                        throw;
                    }
                }
            }
        }

        private async Task UnregisterEventAsync(string eventId, string userId)
        {
            using (var connection = new NpgsqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                using (var transaction = connection.BeginTransaction())
                {
                    try
                    {
                        var eventGuid = Guid.Parse(eventId);
                        var parameters = new { EventId = eventGuid, UserId = userId };

                        _logger.LogInformation("Checking if registration exists for EventId: {EventId} and UserId: {UserId}", eventGuid, userId);

                        // Check if the registration already exists
                        var checkRegistrationQuery = "SELECT * FROM \"EventRegistrations\" WHERE \"EventId\" = @EventId AND \"UserId\" = @UserId";
                        var registration = await connection.QuerySingleOrDefaultAsync(checkRegistrationQuery, parameters, transaction);

                        if (registration != null)
                        {
                            _logger.LogInformation("Registration found for EventId: {EventId} and UserId: {UserId}, updating action to 'Unregister'", eventGuid, userId);

                            // If registration exists, update the action to 'Unregister'
                            var updateRegistrationQuery = "UPDATE \"EventRegistrations\" SET \"Action\" = 'Unregister' WHERE \"EventId\" = @EventId AND \"UserId\" = @UserId";
                            await connection.ExecuteAsync(updateRegistrationQuery, parameters, transaction);
                        }
                        else
                        {
                            _logger.LogInformation("No existing registration found for EventId: {EventId} and UserId: {UserId}, inserting 'Unregister' action", eventGuid, userId);

                            // If registration does not exist, insert the unregister action
                            var insertUnregisterActionQuery = "INSERT INTO \"EventRegistrations\" (\"EventId\", \"UserId\", \"Action\") VALUES (@EventId, @UserId, 'Unregister')";
                            await connection.ExecuteAsync(insertUnregisterActionQuery, parameters, transaction);
                        }

                        transaction.Commit();
                        _logger.LogInformation("Unregistration process completed for EventId: {EventId} and UserId: {UserId}", eventGuid, userId);
                    }
                    catch (Exception ex)
                    {
                        transaction.Rollback();
                        _logger.LogError("Error during unregistration: {ErrorMessage}", ex.Message);
                        throw;
                    }
                }
            }
        }


        private async Task<string> GetUserPrincipalNameAsync(string userId)
        {
            var user = await _graphClient.Users[userId].GetAsync();
            var userMail = user?.Mail;
            if (string.IsNullOrEmpty(userMail))
            {
                throw new Exception("User not provide information about email adress");
            }
            return userMail;
        }

        private async Task SendEmailAsync(string userMail, string subject, string message)
        {
            var client = new SendGridClient(_sendGridApiKey);
            var from = new EmailAddress(_fromEmail, "Event Management");
            var to = new EmailAddress(userMail);
            var plainTextContent = message;
            var htmlContent = $"<html><body>{message}</body></html>";
            var msg = MailHelper.CreateSingleEmail(from, to, subject, plainTextContent, htmlContent);

            try
            {
                var response = await client.SendEmailAsync(msg);
                if (response.StatusCode == System.Net.HttpStatusCode.OK || response.StatusCode == System.Net.HttpStatusCode.Accepted)
                {
                    _logger.LogInformation("Email sent to {UserEmail}", userMail);
                }
                else
                {
                    _logger.LogError("Failed to send email to {UserEmail}. StatusCode: {StatusCode}", userMail, response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to send email to {UserEmail}. Error: {ErrorMessage}", userMail, ex.Message);
            }
        }

        public class EventRegistrationDto
        {
            public Guid EventId { get; set; }
            public string? UserId { get; set; }
            public string? Action { get; set; }
        }
    }
}

